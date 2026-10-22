using FluentValidation;
using Koinon.Application.DTOs.Files;
using SkiaSharp;

namespace Koinon.Application.Validators;

/// <summary>
/// Validator for file upload requests.
/// MVP restriction: Only image uploads are supported initially (for person photos).
/// This will be expanded to support additional file types in future releases.
/// </summary>
public class UploadFileRequestValidator : AbstractValidator<UploadFileRequest>
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10MB

    // MVP restriction: Only image types allowed initially
    private static readonly string[] _allowedImageMimeTypes =
    [
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/webp",
        "image/gif"
    ];

    public UploadFileRequestValidator()
    {
        RuleFor(x => x.Stream)
            .NotNull()
            .WithMessage("File stream is required");

        RuleFor(x => x.FileName)
            .NotEmpty()
            .WithMessage("File name is required");

        RuleFor(x => x.ContentType)
            .NotEmpty()
            .WithMessage("Content type is required");

        RuleFor(x => x.Length)
            .GreaterThan(0)
            .WithMessage("File must not be empty")
            .LessThanOrEqualTo(MaxFileSizeBytes)
            .WithMessage($"File size must not exceed {MaxFileSizeBytes / 1024 / 1024}MB");

        RuleFor(x => x.ContentType)
            .Must(BeAllowedImageType)
            .WithMessage($"File must be one of the following types: {string.Join(", ", _allowedImageMimeTypes)}");

        // Validate actual file content using magic bytes (not just Content-Type header)
        RuleFor(x => x.Stream)
            .Must(BeValidImageFile)
            .WithMessage("File is not a valid image. The file content does not match an allowed image format.");

        RuleFor(x => x.Description)
            .MaximumLength(1000)
            .WithMessage("Description must not exceed 1000 characters");
    }

    private static bool BeAllowedImageType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        return _allowedImageMimeTypes.Contains(contentType.ToLowerInvariant());
    }

    private static bool BeValidImageFile(Stream? stream)
    {
        if (stream == null || !stream.CanSeek)
        {
            return false;
        }

        try
        {
            // Save the current position
            var originalPosition = stream.Position;

            try
            {
                // Try to identify the image using SkiaSharp
                // This validates magic bytes and basic image structure
                // disposeManagedStream false: the stream is read again after validation
                using var skStream = new SKManagedStream(stream, false);
                using var codec = SKCodec.Create(skStream);

                // If we got a codec, it's a valid image format
                return codec != null;
            }
            finally
            {
                // Always reset stream position for subsequent operations
                stream.Position = originalPosition;
            }
        }
        catch
        {
            // If SkiaSharp can't identify it, it's not a valid image
            return false;
        }
    }
}
