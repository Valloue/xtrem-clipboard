using System.Buffers.Binary;
using System.Security.Cryptography;

namespace XtremClipboard.Services;

/// <summary>
/// Empreinte d'une image = SHA-256 de la largeur, de la hauteur et des pixels BGRA.
/// Le PNG ré-encodé n'est pas stable octet à octet : deux encodages du même bitmap
/// ne doivent pas compter comme deux images.
/// </summary>
public static class ImageContentHasher
{
    /// <summary>
    /// Calcule l'empreinte. Seuls les <c>largeur × hauteur × 4</c> premiers octets sont lus
    /// (pixels BGRA serrés, sans padding de stride).
    /// </summary>
    public static string HashBgra(int width, int height, ReadOnlySpan<byte> bgraPixels)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        var expected = checked(width * height * 4);
        if (bgraPixels.Length < expected)
            throw new ArgumentException("Le tampon BGRA est plus court que largeur × hauteur × 4.", nameof(bgraPixels));

        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, width);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], height);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header);
        hash.AppendData(bgraPixels[..expected]);
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
