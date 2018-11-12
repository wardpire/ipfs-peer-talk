﻿using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PeerTalk.Cryptography
{
    /// <summary>
    ///   An asymmetric key.
    /// </summary>
    public class Key
    {
        string signingAlgorithmName = "SHA-256withRSA";

        AsymmetricKeyParameter publicKey;
        AsymmetricKeyParameter privateKey;

        public void Verify(byte[] data, byte[] signature)
        {
            var signer = SignerUtilities.GetSigner(signingAlgorithmName);
            signer.Init(false, publicKey);
            signer.BlockUpdate(data, 0, data.Length);
            if (!signer.VerifySignature(signature))
                throw new InvalidDataException("Data does not match the signature.");
        }

        public byte[] Sign(byte[] data)
        {
            var signer = SignerUtilities.GetSigner(signingAlgorithmName);
            signer.Init(true, privateKey);
            signer.BlockUpdate(data, 0, data.Length);
            return signer.GenerateSignature();
        }

        /// <summary>
        ///   Create a public key from the IPFS message.
        /// </summary>
        /// <param name="bytes">
        ///   The IPFS encoded protobuf PublicKey message.
        /// </param>
        /// <returns></returns>
        public static Key CreatePublicKeyFromIpfs(byte[] bytes)
        {
            var key = new Key();

            var ms = new MemoryStream(bytes, false);
            var ipfsKey = ProtoBuf.Serializer.Deserialize<PublicKeyMessage>(ms);

            // For RSA the data is the SPKI
            if (ipfsKey.Type != KeyType.RSA)
                throw new InvalidDataException($"Expected RSA type, not {ipfsKey.Type}.");
            key.publicKey = PublicKeyFactory.CreateKey(ipfsKey.Data);
            
            return key;
        }

        /// <summary>
        ///   Create the key from the Bouncy Castle private key.
        /// </summary>
        /// <param name="privateKey"></param>
        /// <returns></returns>
        public static Key CreatePrivateKey(AsymmetricKeyParameter privateKey)
        {
            var key = new Key();
            key.privateKey = privateKey;

            // Get the public key from the private key.
            if (privateKey is RsaPrivateCrtKeyParameters rsa)
            {
                key.publicKey = new RsaKeyParameters(false, rsa.Modulus, rsa.PublicExponent);
            }
            if (key.publicKey == null)
                throw new NotSupportedException($"The key type {privateKey.GetType().Name} is not supported.");

            return key;
        }

        enum KeyType
        {
            RSA = 0,
            Ed25519 = 1,
            Secp256k1 = 2,
            ECDH = 4,
        }

        [ProtoContract]
        class PublicKeyMessage
        {
            [ProtoMember(1, IsRequired = true)]
            public KeyType Type;
            [ProtoMember(2, IsRequired = true)]
            public byte[] Data;
        }

        [ProtoContract]
        class PrivateKeyMessage
        {
            [ProtoMember(1, IsRequired = true)]
            public KeyType Type;
            [ProtoMember(2, IsRequired = true)]
            public byte[] Data;
        }

    }
}