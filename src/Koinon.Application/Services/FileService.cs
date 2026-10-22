using Koinon.Application.DTOs;
using Koinon.Application.DTOs.Files;
using Koinon.Application.Interfaces;
using Koinon.Domain.Data;
using Koinon.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Koinon.Application.Services;

/// <summary>
/// Service for managing file uploads and downloads.
/// </summary>
public class FileService : IFileService
{
    private readonly IApplicationDbContext _context;
    private readonly IFileStorageService _storageService;
    private readonly ILogger<FileService> _logger;

    public FileService(
        IApplicationDbContext context,
        IFileStorageService storageService,
        ILogger<FileService> logger)
    {
        _context = context;
        _storageService = storageService;
        _logger = logger;
    }

    public async Task<FileMetadataDto> UploadFileAsync(UploadFileRequest request, CancellationToken cancellationToken = default)
    {
        string? storageKey = null;

        try
        {
            // Create a memory stream copy to avoid disposal issues and allow seeking
            using var memoryStream = new MemoryStream();
            await request.Stream.CopyToAsync(memoryStream, cancellationToken);
            memoryStream.Position = 0;

            // Store the file
            storageKey = await _storageService.StoreFileAsync(memoryStream, request.FileName, request.ContentType, cancellationToken);

            // Get image dimensions if it's an image
            int? width = null;
            int? height = null;

            if (IsImageMimeType(request.ContentType))
            {
                memoryStream.Position = 0; // Reset stream position
                try
                {
                    // disposeManagedStream false: memoryStream stays owned by its using
                    using var skStream = new SKManagedStream(memoryStream, false);
                    using var codec = SKCodec.Create(skStream);
                    using var image = codec == null ? null : SKBitmap.Decode(codec);
                    if (image == null)
                    {
                        // Not a valid image or unsupported format - continue without dimensions
                        _logger.LogWarning("Failed to extract image dimensions for {FileName}", request.FileName);
                    }
                    else
                    {
                        width = image.Width;
                        height = image.Height;
                    }
                }
                catch (Exception ex)
                {
                    // Not a valid image or unsupported format - continue without dimensions
                    _logger.LogWarning(ex, "Failed to extract image dimensions for {FileName}", request.FileName);
                }
            }

            // Decode BinaryFileTypeIdKey if provided
            int? binaryFileTypeId = null;
            if (!string.IsNullOrWhiteSpace(request.BinaryFileTypeIdKey))
            {
                if (IdKeyHelper.TryDecode(request.BinaryFileTypeIdKey, out int decodedId))
                {
                    binaryFileTypeId = decodedId;
                }
            }

            // Create BinaryFile entity
            var binaryFile = new BinaryFile
            {
                FileName = request.FileName,
                MimeType = request.ContentType,
                StorageKey = storageKey,
                FileSizeBytes = request.Length,
                Width = width,
                Height = height,
                BinaryFileTypeId = binaryFileTypeId,
                Description = request.Description,
                CreatedDateTime = DateTime.UtcNow
            };

            _context.BinaryFiles.Add(binaryFile);
            await _context.SaveChangesAsync(cancellationToken);

            return await MapToDtoAsync(binaryFile, cancellationToken);
        }
        catch (Exception)
        {
            // Rollback: delete file from storage if database save failed
            if (storageKey != null)
            {
                try
                {
                    await _storageService.DeleteFileAsync(storageKey, cancellationToken);
                    _logger.LogInformation("Rolled back file storage for {StorageKey} due to database save failure", storageKey);
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogError(rollbackEx, "Failed to rollback file storage for {StorageKey}", storageKey);
                }
            }

            throw;
        }
    }

    public async Task<FileMetadataDto?> GetFileMetadataAsync(string idKey, CancellationToken cancellationToken = default)
    {
        if (!IdKeyHelper.TryDecode(idKey, out int id))
        {
            return null;
        }

        var binaryFile = await _context.BinaryFiles
            .Include(f => f.BinaryFileType)
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

        return binaryFile == null ? null : await MapToDtoAsync(binaryFile, cancellationToken);
    }

    public async Task<(Stream Stream, string FileName, string MimeType)?> DownloadFileAsync(string idKey, CancellationToken cancellationToken = default)
    {
        if (!IdKeyHelper.TryDecode(idKey, out int id))
        {
            return null;
        }

        var binaryFile = await _context.BinaryFiles
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

        if (binaryFile == null)
        {
            return null;
        }

        var stream = await _storageService.GetFileAsync(binaryFile.StorageKey, cancellationToken);
        if (stream == null)
        {
            return null;
        }

        return (stream, binaryFile.FileName, binaryFile.MimeType);
    }

    public async Task<bool> DeleteFileAsync(string idKey, CancellationToken cancellationToken = default)
    {
        if (!IdKeyHelper.TryDecode(idKey, out int id))
        {
            return false;
        }

        // AsTracking: global default is NoTracking (PostgreSqlProvider); without it the
        // BinaryFiles.Remove call below would be a no-op (entity detached) (#708).
        var binaryFile = await _context.BinaryFiles
            .AsTracking()
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

        if (binaryFile == null)
        {
            return false;
        }

        // Delete from database first
        _context.BinaryFiles.Remove(binaryFile);
        await _context.SaveChangesAsync(cancellationToken);

        // Delete from storage after DB success (log error but don't throw if storage fails)
        try
        {
            await _storageService.DeleteFileAsync(binaryFile.StorageKey, cancellationToken);
        }
        catch (Exception ex)
        {
            // Log error but don't throw - orphaned files can be cleaned up later
            _logger.LogError(ex, "Failed to delete file from storage after database deletion: {StorageKey}", binaryFile.StorageKey);
        }

        return true;
    }

    private async Task<FileMetadataDto> MapToDtoAsync(BinaryFile binaryFile, CancellationToken cancellationToken = default)
    {
        // Load BinaryFileType navigation property if not already loaded
        DefinedValueDto? binaryFileTypeDto = null;
        if (binaryFile.BinaryFileTypeId.HasValue)
        {
            if (binaryFile.BinaryFileType != null)
            {
                // Already loaded via Include
                binaryFileTypeDto = MapDefinedValueToDto(binaryFile.BinaryFileType);
            }
            else
            {
                // Load explicitly
                var binaryFileType = await _context.DefinedValues
                    .FirstOrDefaultAsync(dv => dv.Id == binaryFile.BinaryFileTypeId.Value, cancellationToken);

                if (binaryFileType != null)
                {
                    binaryFileTypeDto = MapDefinedValueToDto(binaryFileType);
                }
            }
        }

        return new FileMetadataDto
        {
            IdKey = binaryFile.IdKey,
            FileName = binaryFile.FileName,
            MimeType = binaryFile.MimeType,
            FileSizeBytes = binaryFile.FileSizeBytes,
            Width = binaryFile.Width,
            Height = binaryFile.Height,
            BinaryFileType = binaryFileTypeDto,
            Description = binaryFile.Description,
            CreatedDateTime = binaryFile.CreatedDateTime,
            Url = $"/api/v1/files/{binaryFile.IdKey}"
        };
    }

    private static DefinedValueDto MapDefinedValueToDto(DefinedValue definedValue)
    {
        return new DefinedValueDto
        {
            IdKey = definedValue.IdKey,
            Guid = definedValue.Guid,
            Value = definedValue.Value,
            Description = definedValue.Description,
            IsActive = definedValue.IsActive,
            Order = definedValue.Order
        };
    }

    private static bool IsImageMimeType(string mimeType)
    {
        return mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }
}
