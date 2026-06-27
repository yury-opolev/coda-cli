namespace Coda.Sdk.Serve;

/// <summary>
/// Validates that a serve API key is strong enough to authenticate a socket transport.
/// Entropy-based and charset-aware so strong hex/base64url tokens pass while short or
/// low-variety keys are rejected. Returns a human-readable reason on failure.
/// </summary>
public static class ApiKeyStrength
{
    public const int MinLength = 64;
    public const int MinDistinct = 12;
    public const double MinEntropyBits = 256.0;

    public static (bool Ok, string? Reason) Validate(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return (false, "API key is required.");
        }

        if (key.Length < MinLength)
        {
            return (false, $"API key too short: need ≥ {MinLength} characters.");
        }

        if (IsDegenerate(key))
        {
            return (false, "API key looks degenerate (no effective randomness).");
        }

        var distinct = key.Distinct().Count();
        if (distinct < MinDistinct)
        {
            return (false, $"API key has too little variety ({distinct} distinct characters).");
        }

        var bits = key.Length * System.Math.Log2(PoolSize(key));
        if (bits < MinEntropyBits)
        {
            return (false, $"API key is not strong enough: ~{(int)bits} bits of entropy, need ≥ {(int)MinEntropyBits}.");
        }

        return (true, null);
    }

    private static int PoolSize(string key)
    {
        var lower = false;
        var upper = false;
        var digit = false;
        var asciiSymbol = false;
        var other = false;

        foreach (var c in key)
        {
            if (c is >= 'a' and <= 'z')
            {
                lower = true;
            }
            else if (c is >= 'A' and <= 'Z')
            {
                upper = true;
            }
            else if (c is >= '0' and <= '9')
            {
                digit = true;
            }
            else if (c is >= '!' and <= '~')
            {
                asciiSymbol = true;
            }
            else
            {
                other = true;
            }
        }

        var pool = 0;
        if (lower)
        {
            pool += 26;
        }

        if (upper)
        {
            pool += 26;
        }

        if (digit)
        {
            pool += 10;
        }

        if (asciiSymbol)
        {
            pool += 33;
        }

        if (other)
        {
            pool += 33;
        }

        return System.Math.Max(pool, 2);
    }

    private static bool IsDegenerate(string key)
    {
        if (key.All(c => c == key[0]))
        {
            return true;
        }

        var ascending = true;
        var descending = true;
        for (var i = 1; i < key.Length; i++)
        {
            if (key[i] != key[i - 1] + 1)
            {
                ascending = false;
            }

            if (key[i] != key[i - 1] - 1)
            {
                descending = false;
            }
        }

        return ascending || descending;
    }
}
