using System;
using System.Security.Cryptography;
using System.Text;

namespace Feed;

/// <summary>SCRAM-SHA-256 client for the Postgres startup handshake.</summary>
internal sealed class Scram
{
    private readonly string _password;
    private readonly string _nonce;
    private readonly string _clientFirstBare;
    private string? _serverSignature;

    public Scram(string password)
    {
        _password = password;
        Span<byte> random = stackalloc byte[18];
        RandomNumberGenerator.Fill(random);
        _nonce = Convert.ToBase64String(random);
        _clientFirstBare = "n=,r=" + _nonce;
    }

    public byte[] ClientFirst() => Encoding.UTF8.GetBytes("n,," + _clientFirstBare);

    public byte[] ClientFinal(string serverFirst)
    {
        string combinedNonce = "";
        string saltText = "";
        int iterations = 0;

        foreach (string part in serverFirst.Split(','))
        {
            if (part.StartsWith("r=", StringComparison.Ordinal))
            {
                combinedNonce = part[2..];
            }
            else if (part.StartsWith("s=", StringComparison.Ordinal))
            {
                saltText = part[2..];
            }
            else if (part.StartsWith("i=", StringComparison.Ordinal))
            {
                iterations = int.Parse(part[2..]);
            }
        }

        if (!combinedNonce.StartsWith(_nonce, StringComparison.Ordinal) || saltText.Length == 0 || iterations <= 0)
        {
            throw new InvalidOperationException("Postgres sent a bad SCRAM challenge");
        }

        byte[] salt = Convert.FromBase64String(saltText);
        byte[] saltedPassword = Rfc2898DeriveBytes.Pbkdf2(
            _password, salt, iterations, HashAlgorithmName.SHA256, 32);

        byte[] clientKey = HMACSHA256.HashData(saltedPassword, "Client Key"u8);
        byte[] storedKey = SHA256.HashData(clientKey);

        string clientFinalWithoutProof = "c=biws,r=" + combinedNonce;
        string authMessage = _clientFirstBare + "," + serverFirst + "," + clientFinalWithoutProof;
        byte[] authBytes = Encoding.UTF8.GetBytes(authMessage);
        byte[] clientSignature = HMACSHA256.HashData(storedKey, authBytes);
        byte[] proof = new byte[clientKey.Length];
        for (int i = 0; i < proof.Length; i++)
        {
            proof[i] = (byte)(clientKey[i] ^ clientSignature[i]);
        }

        byte[] serverKey = HMACSHA256.HashData(saltedPassword, "Server Key"u8);
        _serverSignature = Convert.ToBase64String(HMACSHA256.HashData(serverKey, authBytes));

        return Encoding.UTF8.GetBytes(clientFinalWithoutProof + ",p=" + Convert.ToBase64String(proof));
    }

    public void VerifyServerFinal(string serverFinal)
    {
        string? verifier = null;
        foreach (string part in serverFinal.Split(','))
        {
            if (part.StartsWith("v=", StringComparison.Ordinal))
            {
                verifier = part[2..];
            }
        }

        if (verifier is null || _serverSignature is null)
        {
            throw new InvalidOperationException("Postgres sent a bad SCRAM final message");
        }

        byte[] expected = Encoding.ASCII.GetBytes(_serverSignature);
        byte[] actual = Encoding.ASCII.GetBytes(verifier);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw new InvalidOperationException("Postgres SCRAM server signature did not match");
        }
    }
}
