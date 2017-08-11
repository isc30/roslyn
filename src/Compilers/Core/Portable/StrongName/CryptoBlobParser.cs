﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using Microsoft.CodeAnalysis.Collections;
using Roslyn.Utilities;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Security.Cryptography;

namespace Microsoft.CodeAnalysis
{
    internal static class CryptoBlobParser
    {
        private enum AlgorithmClass
        {
            Signature = 1,
            Hash = 4,
        }

        private enum AlgorithmSubId
        {
            Sha1Hash = 4,
            MacHash = 5,
            RipeMdHash = 6,
            RipeMd160Hash = 7,
            Ssl3ShaMD5Hash = 8,
            HmacHash = 9,
            Tls1PrfHash = 10,
            HashReplacOwfHash = 11,
            Sha256Hash = 12,
            Sha384Hash = 13,
            Sha512Hash = 14,
        }

        private struct AlgorithmId
        {
            // From wincrypt.h
            private const int AlgorithmClassOffset = 13;
            private const int AlgorithmClassMask = 0x7;
            private const int AlgorithmSubIdOffset = 0;
            private const int AlgorithmSubIdMask = 0x1ff;

            private readonly uint _flags;

            public const int RsaSign = 0x00002400;
            public const int Sha = 0x00008004;

            public bool IsSet
            {
                get { return _flags != 0; }
            }

            public AlgorithmClass Class
            {
                get { return (AlgorithmClass)((_flags >> AlgorithmClassOffset) & AlgorithmClassMask); }
            }

            public AlgorithmSubId SubId
            {
                get { return (AlgorithmSubId)((_flags >> AlgorithmSubIdOffset) & AlgorithmSubIdMask); }
            }

            public AlgorithmId(uint flags)
            {
                _flags = flags;
            }
        }

        // From ECMAKey.h
        private static readonly ImmutableArray<byte> s_ecmaKey = ImmutableArray.Create(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0 });

        private const int SnPublicKeyBlobSize = 13;

        // From wincrypt.h
        private const byte PublicKeyBlobId = 0x06;
        private const byte PrivateKeyBlobId = 0x07;

        // internal for testing
        internal const int s_publicKeyHeaderSize = SnPublicKeyBlobSize - 1;

        // From StrongNameInternal.cpp
        // Checks to see if a public key is a valid instance of a PublicKeyBlob as
        // defined in StongName.h
        internal static bool IsValidPublicKey(ImmutableArray<byte> blob)
        {
            // The number of public key bytes must be at least large enough for the header and one byte of data.
            if (blob.IsDefault || blob.Length < s_publicKeyHeaderSize + 1)
            {
                return false;
            }

            BinaryReader blobReader = new BinaryReader(new MemoryStream(blob.DangerousGetUnderlyingArray()));

            // Signature algorithm ID
            var sigAlgId = blobReader.ReadUInt32();
            // Hash algorithm ID
            var hashAlgId = blobReader.ReadUInt32();
            // Size of public key data in bytes, not including the header
            var publicKeySize = blobReader.ReadUInt32();
            // publicKeySize bytes of public key data
            var publicKey = blobReader.ReadByte();

            // The number of public key bytes must be the same as the size of the header plus the size of the public key data.
            if (blob.Length != s_publicKeyHeaderSize + publicKeySize)
            {
                return false;
            }

            // Check for the ECMA key, which does not obey the invariants checked below.
            if (ByteSequenceComparer.Equals(blob, s_ecmaKey))
            {
                return true;
            }

            // The public key must be in the wincrypto PUBLICKEYBLOB format
            if (publicKey != PublicKeyBlobId)
            {
                return false;
            }

            var signatureAlgorithmId = new AlgorithmId(sigAlgId);
            if (signatureAlgorithmId.IsSet && signatureAlgorithmId.Class != AlgorithmClass.Signature)
            {
                return false;
            }

            var hashAlgorithmId = new AlgorithmId(hashAlgId);
            if (hashAlgorithmId.IsSet && (hashAlgorithmId.Class != AlgorithmClass.Hash || hashAlgorithmId.SubId < AlgorithmSubId.Sha1Hash))
            {
                return false;
            }

            return true;
        }

        private const int BlobHeaderSize = sizeof(byte) + sizeof(byte) + sizeof(ushort) + sizeof(uint);

        private const int RsaPubKeySize = sizeof(uint) + sizeof(uint) + sizeof(uint);

        private const UInt32 RSA1 = 0x31415352;
        private const UInt32 RSA2 = 0x32415352;

        // In wincrypt.h both public and private key blobs start with a
        // PUBLICKEYSTRUC and RSAPUBKEY and then start the key data
        private const int s_offsetToKeyData = BlobHeaderSize + RsaPubKeySize;

        private static ImmutableArray<byte> CreateSnPublicKeyBlob(
            byte type, 
            byte version, 
            uint algId, 
            uint magic, 
            uint bitLen, 
            uint pubExp, 
            byte[] pubKeyData)
        {
            var w = new BlobWriter(3 * sizeof(uint) + s_offsetToKeyData + pubKeyData.Length);
            w.WriteUInt32(AlgorithmId.RsaSign);
            w.WriteUInt32(AlgorithmId.Sha);
            w.WriteUInt32((uint)(s_offsetToKeyData + pubKeyData.Length));

            w.WriteByte(type);
            w.WriteByte(version);
            w.WriteUInt16(0 /* 16 bits of reserved space in the spec */);
            w.WriteUInt32(algId);

            w.WriteUInt32(magic);
            w.WriteUInt32(bitLen);

            // re-add padding for exponent
            w.WriteUInt32(pubExp);

            w.WriteBytes(pubKeyData);

            return w.ToImmutableArray();
        }

        /// <summary>
        /// Try to retrieve the public key from a crypto blob.
        /// </summary>
        /// <remarks>
        /// Can be either a PUBLICKEYBLOB or PRIVATEKEYBLOB. The BLOB must be unencrypted.
        /// </remarks>
        public static bool TryParseKey(ImmutableArray<byte> blob, out ImmutableArray<byte> snKey, out RSAParameters? privateKey)
        {
            privateKey = null;
            snKey = default(ImmutableArray<byte>);

            var asArray = blob.DangerousGetUnderlyingArray();

            if (IsValidPublicKey(blob))
            {
                snKey = blob;
                return true;
            }

            if (blob.Length < BlobHeaderSize + RsaPubKeySize)
            {
                return false;
            }

            try
            {
                BinaryReader br = new BinaryReader(new MemoryStream(asArray));

                byte bType = br.ReadByte();    // BLOBHEADER.bType: Expected to be 0x6 (PUBLICKEYBLOB) or 0x7 (PRIVATEKEYBLOB), though there's no check for backward compat reasons. 
                byte bVersion = br.ReadByte(); // BLOBHEADER.bVersion: Expected to be 0x2, though there's no check for backward compat reasons.
                br.ReadUInt16();               // BLOBHEADER.wReserved
                uint algId = br.ReadUInt32();  // BLOBHEADER.aiKeyAlg
                uint magic = br.ReadUInt32();  // RSAPubKey.magic: Expected to be 0x31415352 ('RSA1') or 0x32415352 ('RSA2') 
                var bitLen = br.ReadUInt32();  // Bit Length for Modulus
                var pubExp = br.ReadUInt32();  // Exponent 
                var modulusLength = (int) (bitLen / 8);


                if (blob.Length - s_offsetToKeyData < modulusLength)
                {
                    return false;
                }

                var modulus = br.ReadBytes((int) bitLen / 8);

                if (!(bType == PrivateKeyBlobId && magic == RSA2) && !(bType == PublicKeyBlobId && magic == RSA1))
                {
                    return false;
                }

                if (bType == PrivateKeyBlobId)
                {
                    RSAParameters rsaParameters;
                    using (var rsa = new RSACryptoServiceProvider())
                    {
                        rsa.ImportCspBlob(asArray);
                        rsaParameters = rsa.ExportParameters(true);
                    }
                    privateKey = rsaParameters;

                    // For snKey, rewrite some of the the parameters
                    algId = AlgorithmId.RsaSign;
                    magic = RSA1;
                } 

                snKey = CreateSnPublicKeyBlob(PublicKeyBlobId, bVersion, algId, RSA1, bitLen, pubExp, modulus);
                return true;
            }
            catch (EndOfStreamException)
            {
                return false;
            }
        }
    }
}
