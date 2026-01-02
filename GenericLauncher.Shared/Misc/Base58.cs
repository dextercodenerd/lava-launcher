using System;
using System.Numerics;
using System.Text;

namespace GenericLauncher.Misc;

public static class Base58
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static string Encode(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        return Encode(bytes);
    }

    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return string.Empty;
        }

        var value = new BigInteger(data, true, true);
        var result = new StringBuilder();

        while (value > 0)
        {
            value = BigInteger.DivRem(value, 58, out var remainder);
            result.Insert(0, Alphabet[(int)remainder]);
        }

        foreach (var b in data)
        {
            if (b == 0)
            {
                result.Insert(0, Alphabet[0]);
            }
            else
            {
                break;
            }
        }

        return result.ToString();
    }
}
