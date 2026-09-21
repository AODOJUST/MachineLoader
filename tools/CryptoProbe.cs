// Probe: does the Unity Mono profile expose SHA256 + RSA verify APIs we can rely on?
using System;
using System.Security.Cryptography;

public class CryptoProbe
{
    static void Main()
    {
        try
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes("probe");
            byte[] h1 = SHA256.Create().ComputeHash(data);
            byte[] h2 = new SHA256Managed().ComputeHash(data);
            Console.WriteLine("SHA256 ok len=" + h1.Length + " managed=" + h2.Length);
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.KeySize = 2048;
                byte[] sig = rsa.SignData(data, "SHA256");
                bool v = rsa.VerifyData(data, "SHA256", sig);
                RSAParameters p = rsa.ExportParameters(false);
                rsa.ImportParameters(p);
                Console.WriteLine("RSA ok verify=" + v + " mod=" + p.Modulus.Length + " exp=" + p.Exponent.Length);
            }
            Console.WriteLine("BigInt present=" + (typeof(System.Numerics.BigInteger) != null));
        }
        catch (Exception e) { Console.WriteLine("CRYPTO PROBE FAIL: " + e.GetType().Name + ": " + e.Message); }
    }
}
