using System.Security.Cryptography;

namespace AIDesignStudio.IntegrationTests;

/// <summary>
/// A minimal RFC 6238 TOTP generator, standing in for the authenticator app.
///
/// <para><b>Why the tests need this.</b> The obvious approach —
/// <c>UserManager.GenerateTwoFactorTokenAsync</c> with the authenticator
/// provider — cannot work: <c>AuthenticatorTokenProvider.GenerateAsync</c>
/// returns an empty string by design, because generating the code is the
/// <i>app's</i> job, not the server's. The server only ever verifies. So a test
/// that wants to prove enrolment works has to compute a real code from the shared
/// key, exactly as a phone would.</para>
///
/// Matches the parameters Identity's authenticator provider verifies against:
/// HMAC-SHA1 over the 30-second Unix time step, dynamic truncation, six digits.
/// </summary>
internal static class Totp
{
    private const int StepSeconds = 30;
    private const int Digits = 6;

    /// <param name="base32SharedKey">
    /// The key as issued by <c>/api/v1/auth/two-factor/enroll</c>, which is the
    /// base32 form Identity hands to authenticator apps.
    /// </param>
    /// <param name="timestamp">The instant to compute for. Defaults to now.</param>
    public static string Generate(string base32SharedKey, DateTimeOffset? timestamp = null)
    {
        var key = Base32Decode(base32SharedKey);
        var step = (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / StepSeconds;

        var counter = BitConverter.GetBytes(step);

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

        var hash = HMACSHA1.HashData(key, counter);

        // Dynamic truncation, RFC 4226 §5.3: the low nibble of the last byte
        // selects a four-byte window, whose top bit is masked off.
        var offset = hash[^1] & 0x0F;

        var binary = ((hash[offset] & 0x7F) << 24)
                     | ((hash[offset + 1] & 0xFF) << 16)
                     | ((hash[offset + 2] & 0xFF) << 8)
                     | (hash[offset + 3] & 0xFF);

        return (binary % (int)Math.Pow(10, Digits))
            .ToString(System.Globalization.CultureInfo.InvariantCulture)
            .PadLeft(Digits, '0');
    }

    /// <summary>
    /// Base32 (RFC 4648) without padding, which is the alphabet Identity uses for
    /// the shared key.
    /// </summary>
    private static byte[] Base32Decode(string input)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var normalized = input.Replace(" ", string.Empty, StringComparison.Ordinal)
            .TrimEnd('=')
            .ToUpperInvariant();

        var bits = 0;
        var accumulator = 0;
        var bytes = new List<byte>(normalized.Length * 5 / 8);

        foreach (var character in normalized)
        {
            var value = Alphabet.IndexOf(character, StringComparison.Ordinal);

            if (value < 0)
            {
                throw new FormatException($"'{character}' is not a base32 character.");
            }

            accumulator = (accumulator << 5) | value;
            bits += 5;

            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((accumulator >> bits) & 0xFF));
            }
        }

        return [.. bytes];
    }
}
