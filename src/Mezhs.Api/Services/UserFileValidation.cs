using Mezhs;

namespace Mezhs.Services;

// Validate locally before a file can be referenced by a queued provider request.
internal static class UserFileValidation
{
    public static void Validate(string name, string? contentType, string path)
    {
        var mime = (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var validType = extension switch
        {
            ".txt" => mime == "text/plain",
            ".md" => mime is "text/markdown" or "text/plain",
            ".csv" => mime == "text/csv",
            ".pdf" => mime == "application/pdf",
            ".docx" => mime == "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".png" => mime == "image/png",
            _ => false
        };
        if (!validType)
            throw new RequestValidationException("Unsupported file format or MIME type. Accepted: TXT, MD, CSV, PDF, DOCX and PNG.");

        if (extension != ".png") return;
        using var stream = File.OpenRead(path);
        var header = new byte[32];
        var length = stream.Read(header);
        var png = extension == ".png" && length >= 29 &&
            header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4)) == 13 &&
            header.AsSpan(12, 4).SequenceEqual("IHDR"u8) &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4)) > 0 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4)) > 0;
        if (!png)
            throw new RequestValidationException("PNG image content is invalid or does not match its type.");
        if (png)
        {
            stream.Seek(-12, SeekOrigin.End);
            var trailer = new byte[12];
            if (stream.Read(trailer) != 12 ||
                !trailer.AsSpan(4, 4).SequenceEqual("IEND"u8) ||
                trailer.AsSpan(0, 4).IndexOfAnyExcept((byte)0) >= 0)
                throw new RequestValidationException("PNG image is truncated.");
        }
    }
}
