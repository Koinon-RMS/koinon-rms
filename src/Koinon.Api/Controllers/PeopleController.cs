using Koinon.Api.Filters;
using Koinon.Application.Common;
using Koinon.Application.DTOs;
using Koinon.Application.DTOs.Files;
using Koinon.Application.DTOs.Giving;
using Koinon.Application.DTOs.Requests;
using Koinon.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkiaSharp;

namespace Koinon.Api.Controllers;

/// <summary>
/// Controller for person management operations.
/// Provides endpoints for searching, creating, updating, and retrieving people.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class PeopleController(
    IPersonService personService,
    IFileService fileService,
    ICheckinAttendanceService checkinAttendanceService,
    ILogger<PeopleController> logger) : ControllerBase
{
    /// <summary>
    /// Searches for people with optional filters and pagination.
    /// </summary>
    /// <param name="query">Full-text search query</param>
    /// <param name="campusId">Filter by campus IdKey</param>
    /// <param name="recordStatusId">Filter by record status IdKey</param>
    /// <param name="connectionStatusId">Filter by connection status IdKey</param>
    /// <param name="includeInactive">Include inactive records</param>
    /// <param name="page">Page number (1-based, default: 1)</param>
    /// <param name="pageSize">Items per page (default: 25, max: 100)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Paginated list of people</returns>
    /// <response code="200">Returns paginated list of people</response>
    [HttpGet]
    [ValidateIdKey]
    [ProducesResponseType(typeof(PagedResult<PersonSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(
        [FromQuery] string? query,
        [FromQuery] string? campusId,
        [FromQuery] string? recordStatusId,
        [FromQuery] string? connectionStatusId,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var parameters = new PersonSearchParameters
        {
            Query = query,
            CampusId = campusId,
            RecordStatusId = recordStatusId,
            ConnectionStatusId = connectionStatusId,
            IncludeInactive = includeInactive,
            Page = page,
            PageSize = pageSize
        };

        var result = await personService.SearchAsync(parameters, ct);

        logger.LogInformation(
            "People search completed: Query={Query}, Page={Page}, PageSize={PageSize}, TotalCount={TotalCount}",
            query, result.Page, result.PageSize, result.TotalCount);

        return Ok(new
        {
            data = result.Items,
            meta = new
            {
                page = result.Page,
                pageSize = result.PageSize,
                totalCount = result.TotalCount,
                totalPages = (int)Math.Ceiling(result.TotalCount / (double)result.PageSize)
            }
        });
    }

    /// <summary>
    /// Gets a person by their IdKey with full details.
    /// </summary>
    /// <param name="idKey">The person's IdKey</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Person details</returns>
    /// <response code="200">Returns person details</response>
    /// <response code="404">Person not found</response>
    [HttpGet("{idKey}")]
    [ValidateIdKey]
    [ProducesResponseType(typeof(PersonDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByIdKey(string idKey, CancellationToken ct = default)
    {
        var person = await personService.GetByIdKeyAsync(idKey, ct);

        if (person == null)
        {
            logger.LogDebug("Person not found: IdKey={IdKey}", idKey);

            return NotFound(new ProblemDetails
            {
                Title = "Person not found",
                Detail = $"No person found with IdKey '{idKey}'",
                Status = StatusCodes.Status404NotFound,
                Instance = HttpContext.Request.Path
            });
        }

        logger.LogDebug("Person retrieved: IdKey={IdKey}, Name={Name}", idKey, person.FullName);

        return Ok(new { data = person });
    }

    /// <summary>
    /// Creates a new person.
    /// </summary>
    /// <param name="request">Person creation details</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Created person details</returns>
    /// <response code="201">Person created successfully</response>
    /// <response code="400">Validation failed</response>
    /// <response code="422">Business rule violation</response>
    [HttpPost]
    [ProducesResponseType(typeof(PersonDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] CreatePersonRequest request, CancellationToken ct = default)
    {
        var result = await personService.CreateAsync(request, ct);

        if (result.IsFailure)
        {
            logger.LogWarning(
                "Failed to create person: Code={Code}, Message={Message}",
                result.Error!.Code, result.Error.Message);

            return result.Error.Code switch
            {
                "VALIDATION_ERROR" => BadRequest(new ProblemDetails
                {
                    Title = result.Error.Message,
                    Detail = result.Error.Details != null
                        ? string.Join("; ", result.Error.Details.SelectMany(kvp => kvp.Value))
                        : null,
                    Status = StatusCodes.Status400BadRequest,
                    Instance = HttpContext.Request.Path,
                    Extensions = { ["errors"] = result.Error.Details }
                }),
                _ => UnprocessableEntity(new ProblemDetails
                {
                    Title = result.Error.Code,
                    Detail = result.Error.Message,
                    Status = StatusCodes.Status422UnprocessableEntity,
                    Instance = HttpContext.Request.Path
                })
            };
        }

        var person = result.Value!;

        logger.LogInformation(
            "Person created successfully: IdKey={IdKey}, Name={Name}",
            person.IdKey, person.FullName);

        return CreatedAtAction(
            nameof(GetByIdKey),
            new { idKey = person.IdKey },
            new { data = person });
    }

    /// <summary>
    /// Updates an existing person.
    /// </summary>
    /// <param name="idKey">The person's IdKey</param>
    /// <param name="request">Person update details</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Updated person details</returns>
    /// <response code="200">Person updated successfully</response>
    /// <response code="400">Validation failed</response>
    /// <response code="404">Person not found</response>
    /// <response code="422">Business rule violation</response>
    [HttpPut("{idKey}")]
    [ValidateIdKey]
    [ProducesResponseType(typeof(PersonDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Update(
        string idKey,
        [FromBody] UpdatePersonRequest request,
        CancellationToken ct = default)
    {
        var result = await personService.UpdateAsync(idKey, request, ct);

        if (result.IsFailure)
        {
            logger.LogDebug(
                "Failed to update person: IdKey={IdKey}, Code={Code}, Message={Message}",
                idKey, result.Error!.Code, result.Error.Message);

            return result.Error.Code switch
            {
                "NOT_FOUND" => NotFound(new ProblemDetails
                {
                    Title = "Person not found",
                    Detail = result.Error.Message,
                    Status = StatusCodes.Status404NotFound,
                    Instance = HttpContext.Request.Path
                }),
                "VALIDATION_ERROR" => BadRequest(new ProblemDetails
                {
                    Title = result.Error.Message,
                    Detail = result.Error.Details != null
                        ? string.Join("; ", result.Error.Details.SelectMany(kvp => kvp.Value))
                        : null,
                    Status = StatusCodes.Status400BadRequest,
                    Instance = HttpContext.Request.Path,
                    Extensions = { ["errors"] = result.Error.Details }
                }),
                _ => UnprocessableEntity(new ProblemDetails
                {
                    Title = result.Error.Code,
                    Detail = result.Error.Message,
                    Status = StatusCodes.Status422UnprocessableEntity,
                    Instance = HttpContext.Request.Path
                })
            };
        }

        var person = result.Value!;

        logger.LogInformation(
            "Person updated successfully: IdKey={IdKey}, Name={Name}",
            person.IdKey, person.FullName);

        return Ok(new { data = person });
    }

    /// <summary>
    /// Soft-deletes a person (sets record status to inactive).
    /// </summary>
    /// <param name="idKey">The person's IdKey</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>No content</returns>
    /// <response code="204">Person deleted successfully</response>
    /// <response code="404">Person not found</response>
    /// <response code="422">Business rule violation</response>
    [HttpDelete("{idKey}")]
    [ValidateIdKey]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Delete(string idKey, CancellationToken ct = default)
    {
        var result = await personService.DeleteAsync(idKey, ct);

        if (result.IsFailure)
        {
            logger.LogDebug(
                "Failed to delete person: IdKey={IdKey}, Code={Code}, Message={Message}",
                idKey, result.Error!.Code, result.Error.Message);

            return result.Error.Code switch
            {
                "NOT_FOUND" => NotFound(new ProblemDetails
                {
                    Title = "Person not found",
                    Detail = result.Error.Message,
                    Status = StatusCodes.Status404NotFound,
                    Instance = HttpContext.Request.Path
                }),
                _ => UnprocessableEntity(new ProblemDetails
                {
                    Title = result.Error.Code,
                    Detail = result.Error.Message,
                    Status = StatusCodes.Status422UnprocessableEntity,
                    Instance = HttpContext.Request.Path
                })
            };
        }

        logger.LogInformation("Person soft-deleted successfully: IdKey={IdKey}", idKey);

        return NoContent();
    }

    /// <summary>
    /// Gets the groups a person belongs to (excluding family groups).
    /// </summary>
    /// <param name="idKey">The person's IdKey</param>
    /// <param name="page">Page number (1-based, default: 1)</param>
    /// <param name="pageSize">Items per page (default: 25, max: 100)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Paginated list of group memberships</returns>
    /// <response code="200">Returns paginated list of group memberships</response>
    [HttpGet("{idKey}/groups")]
    [ValidateIdKey]
    [ProducesResponseType(typeof(PagedResult<PersonGroupMembershipDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetGroups(
        string idKey,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var result = await personService.GetGroupsAsync(idKey, page, pageSize, ct);

        logger.LogInformation(
            "Groups retrieved for person: IdKey={IdKey}, Page={Page}, PageSize={PageSize}, TotalCount={TotalCount}",
            idKey, result.Page, result.PageSize, result.TotalCount);

        return Ok(new
        {
            data = result.Items,
            meta = new
            {
                page = result.Page,
                pageSize = result.PageSize,
                totalCount = result.TotalCount,
                totalPages = (int)Math.Ceiling(result.TotalCount / (double)result.PageSize)
            }
        });
    }

    /// <summary>
    /// Gets a person's family with all members.
    /// </summary>
    /// <param name="idKey">The person's IdKey</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Family details with members, or null if person has no family</returns>
    /// <response code="200">Returns family details or null if person has no family</response>
    /// <response code="404">Person not found</response>
    [HttpGet("{idKey}/family")]
    [ValidateIdKey]
    [ProducesResponseType(typeof(FamilySummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFamily(string idKey, CancellationToken ct = default)
    {
        var result = await personService.GetFamilyAsync(idKey, ct);

        if (result.IsFailure)
        {
            logger.LogDebug("Person not found: IdKey={IdKey}", idKey);

            return NotFound(new ProblemDetails
            {
                Title = "Person not found",
                Detail = result.Error!.Message,
                Status = StatusCodes.Status404NotFound,
                Instance = HttpContext.Request.Path
            });
        }

        if (result.Value is null)
        {
            logger.LogDebug("Person has no family: IdKey={IdKey}", idKey);
        }
        else
        {
            logger.LogDebug(
                "Family retrieved for person: IdKey={IdKey}, FamilyName={FamilyName}",
                idKey, result.Value.Name);
        }

        return Ok(new { data = result.Value });
    }

    /// <summary>
    /// Gets the attendance history for a person.
    /// </summary>
    [HttpGet("{idKey}/attendance")]
    [ValidateIdKey]
    [ProducesResponseType(typeof(IReadOnlyList<AttendanceSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAttendanceHistory(
        string idKey,
        [FromQuery] int days = 90,
        CancellationToken ct = default)
    {
        var history = await checkinAttendanceService.GetPersonAttendanceHistoryAsync(idKey, days, ct);
        logger.LogDebug("Attendance history retrieved for person: IdKey={IdKey}, Days={Days}, Count={Count}", idKey, days, history.Count);
        return Ok(new { data = history });
    }

    /// <summary>
    /// Gets the giving summary for a person, including YTD total, last contribution date,
    /// and the 10 most recent contribution line items.
    /// </summary>
    /// <param name="idKey">The person's IdKey</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Giving summary</returns>
    /// <response code="200">Returns giving summary</response>
    /// <response code="404">Person not found</response>
    [HttpGet("{idKey}/giving-summary")]
    [ValidateIdKey]
    [ProducesResponseType(typeof(PersonGivingSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetGivingSummary(string idKey, CancellationToken ct = default)
    {
        var result = await personService.GetGivingSummaryAsync(idKey, ct);

        if (result.IsFailure)
        {
            logger.LogDebug("Person not found for giving summary: IdKey={IdKey}", idKey);

            return NotFound(new ProblemDetails
            {
                Title = "Person not found",
                Detail = result.Error!.Message,
                Status = StatusCodes.Status404NotFound,
                Instance = HttpContext.Request.Path
            });
        }

        logger.LogDebug(
            "Giving summary retrieved for person: IdKey={IdKey}, YTD={YtdTotal}",
            idKey, result.Value!.YearToDateTotal);

        return Ok(new { data = result.Value });
    }

    /// <summary>
    /// Gets all notes for a person, ordered by note date descending.
    /// </summary>
    /// <param name="idKey">The person's IdKey</param>
    /// <param name="page">Page number (1-based, default 1)</param>
    /// <param name="pageSize">Items per page (1-100, default 25)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of person notes</returns>
    /// <response code="200">Returns list of notes</response>
    [HttpGet("{idKey}/notes")]
    [ValidateIdKey]
    [ProducesResponseType(typeof(IEnumerable<PersonNoteDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<PersonNoteDto>>> GetPersonNotes(
        string idKey,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var notes = await personService.GetNotesAsync(idKey, page, pageSize, ct);

        logger.LogDebug("Notes retrieved for person: IdKey={IdKey}", idKey);

        return Ok(new { data = notes });
    }

    /// <summary>
    /// Creates a new note on a person record.
    /// </summary>
    /// <param name="idKey">The person's IdKey</param>
    /// <param name="request">Note creation details</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Created note</returns>
    /// <response code="201">Note created successfully</response>
    /// <response code="404">Person not found</response>
    [HttpPost("{idKey}/notes")]
    [ValidateIdKey]
    [ProducesResponseType(typeof(PersonNoteDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PersonNoteDto>> CreatePersonNote(
        string idKey,
        [FromBody] CreatePersonNoteRequest request,
        CancellationToken ct)
    {
        try
        {
            var note = await personService.CreateNoteAsync(idKey, request, ct);

            logger.LogInformation(
                "Note created for person: IdKey={IdKey}, NoteIdKey={NoteIdKey}",
                idKey, note.IdKey);

            return CreatedAtAction(
                nameof(GetPersonNotes),
                new { idKey },
                new { data = note });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Person not found",
                Detail = $"No person found with IdKey '{idKey}'",
                Status = StatusCodes.Status404NotFound,
                Instance = HttpContext.Request.Path
            });
        }
    }

    /// <summary>
    /// Updates an existing note on a person record.
    /// </summary>
    /// <param name="idKey">The person's IdKey</param>
    /// <param name="noteIdKey">The note's IdKey</param>
    /// <param name="request">Note update details</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Updated note</returns>
    /// <response code="200">Note updated successfully</response>
    /// <response code="404">Person or note not found</response>
    [HttpPut("{idKey}/notes/{noteIdKey}")]
    [ValidateIdKey]
    [ProducesResponseType(typeof(PersonNoteDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PersonNoteDto>> UpdatePersonNote(
        string idKey,
        string noteIdKey,
        [FromBody] UpdatePersonNoteRequest request,
        CancellationToken ct)
    {
        try
        {
            var note = await personService.UpdateNoteAsync(idKey, noteIdKey, request, ct);

            logger.LogInformation(
                "Note updated: IdKey={IdKey}, NoteIdKey={NoteIdKey}",
                idKey, noteIdKey);

            return Ok(new { data = note });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Note not found",
                Detail = $"No note '{noteIdKey}' found for person '{idKey}'",
                Status = StatusCodes.Status404NotFound,
                Instance = HttpContext.Request.Path
            });
        }
    }

    /// <summary>
    /// Deletes a note from a person record.
    /// </summary>
    /// <param name="idKey">The person's IdKey</param>
    /// <param name="noteIdKey">The note's IdKey</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>No content</returns>
    /// <response code="204">Note deleted successfully</response>
    /// <response code="404">Person or note not found</response>
    [HttpDelete("{idKey}/notes/{noteIdKey}")]
    [ValidateIdKey]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeletePersonNote(string idKey, string noteIdKey, CancellationToken ct)
    {
        try
        {
            await personService.DeleteNoteAsync(idKey, noteIdKey, ct);

            logger.LogInformation(
                "Note deleted: IdKey={IdKey}, NoteIdKey={NoteIdKey}",
                idKey, noteIdKey);

            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Note not found",
                Detail = $"No note '{noteIdKey}' found for person '{idKey}'",
                Status = StatusCodes.Status404NotFound,
                Instance = HttpContext.Request.Path
            });
        }
    }

    /// <summary>
    /// Maximum photo file size in bytes (5MB).
    /// </summary>
    private const long MaxPhotoSizeBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Uploads a photo for a person.
    /// </summary>
    /// <param name="idKey">The person's IdKey</param>
    /// <param name="file">The photo file to upload</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Updated person details with photo URL</returns>
    /// <response code="200">Photo uploaded successfully</response>
    /// <response code="400">Validation failed</response>
    /// <response code="404">Person not found</response>
    [HttpPost("{idKey}/photo")]
    [ValidateIdKey]
    [RequestSizeLimit(5_242_880)] // 5MB limit for photos
    [ProducesResponseType(typeof(PersonDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UploadPhoto(
        string idKey,
        IFormFile file,
        CancellationToken ct = default)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "File is required",
                Status = StatusCodes.Status400BadRequest,
                Instance = HttpContext.Request.Path
            });
        }

        // CRITICAL: Validate file size before processing
        if (file.Length > MaxPhotoSizeBytes)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = $"File size exceeds maximum allowed size of {MaxPhotoSizeBytes / 1024 / 1024}MB",
                Status = StatusCodes.Status400BadRequest,
                Instance = HttpContext.Request.Path
            });
        }

        // CRITICAL: Validate file extension whitelist
        var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif" };
        var fileExtension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (string.IsNullOrEmpty(fileExtension) || !allowedExtensions.Contains(fileExtension))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Only .jpg, .jpeg, .png, and .gif files are allowed",
                Status = StatusCodes.Status400BadRequest,
                Instance = HttpContext.Request.Path
            });
        }

        // Validate file type (images only) via MIME type
        if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Only image files are allowed for person photos",
                Status = StatusCodes.Status400BadRequest,
                Instance = HttpContext.Request.Path
            });
        }

        // BLOCKER: Validate file signature using SkiaSharp (magic bytes check)
        try
        {
            using var validationStream = file.OpenReadStream();
            // disposeManagedStream false: validationStream stays owned by its using
            using var skStream = new SKManagedStream(validationStream, false);
            using var codec = SKCodec.Create(skStream);
            if (codec == null)
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Validation failed",
                    Detail = "File is not a valid image. The file may be corrupted or have an incorrect extension.",
                    Status = StatusCodes.Status400BadRequest,
                    Instance = HttpContext.Request.Path
                });
            }

            using var image = SKBitmap.Decode(codec);
            if (image == null)
            {
                logger.LogWarning("Image validation failed for file: {FileName}", file.FileName);
                return BadRequest(new ProblemDetails
                {
                    Title = "Validation failed",
                    Detail = "Unable to process the image file",
                    Status = StatusCodes.Status400BadRequest,
                    Instance = HttpContext.Request.Path
                });
            }
            // If SkiaSharp can decode it, it's a valid image file
            // Stream is disposed here after validation
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Image validation failed for file: {FileName}", file.FileName);
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Unable to process the image file",
                Status = StatusCodes.Status400BadRequest,
                Instance = HttpContext.Request.Path
            });
        }

        FileMetadataDto? uploadedFile = null;

        try
        {
            // BLOCKER FIX: Open stream AFTER validation is complete
            // This ensures the validation stream is fully disposed before we open a new one
            await using var fileStream = file.OpenReadStream();

            var uploadRequest = new UploadFileRequest
            {
                Stream = fileStream,
                FileName = file.FileName,
                ContentType = file.ContentType,
                Length = file.Length,
                Description = $"Photo for person {idKey}"
            };

            uploadedFile = await fileService.UploadFileAsync(uploadRequest, ct);

            // Update person's PhotoId
            var result = await personService.UpdatePhotoAsync(idKey, uploadedFile.IdKey, ct);

            if (result.IsFailure)
            {
                // BLOCKER FIX: Wrap cleanup in try-catch to prevent transaction rollback failure
                try
                {
                    await fileService.DeleteFileAsync(uploadedFile.IdKey, ct);
                }
                catch (Exception cleanupEx)
                {
                    logger.LogError(cleanupEx,
                        "Failed to cleanup uploaded file after person update failure: PhotoIdKey={PhotoIdKey}",
                        uploadedFile.IdKey);
                    // Continue - the file will be orphaned but person update failure is more important
                }

                logger.LogWarning(
                    "Failed to update person photo: IdKey={IdKey}, Code={Code}, Message={Message}",
                    idKey, result.Error!.Code, result.Error.Message);

                return result.Error.Code switch
                {
                    "NOT_FOUND" => NotFound(new ProblemDetails
                    {
                        Title = "Person not found",
                        Detail = result.Error.Message,
                        Status = StatusCodes.Status404NotFound,
                        Instance = HttpContext.Request.Path
                    }),
                    _ => BadRequest(new ProblemDetails
                    {
                        Title = result.Error.Code,
                        Detail = result.Error.Message,
                        Status = StatusCodes.Status400BadRequest,
                        Instance = HttpContext.Request.Path
                    })
                };
            }

            logger.LogInformation(
                "Photo uploaded successfully for person: IdKey={IdKey}, PhotoIdKey={PhotoIdKey}",
                idKey, uploadedFile.IdKey);

            return Ok(new { data = result.Value });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to upload photo for person: IdKey={IdKey}", idKey);

            // BLOCKER FIX: Attempt cleanup if upload succeeded but something else failed
            if (uploadedFile != null)
            {
                try
                {
                    await fileService.DeleteFileAsync(uploadedFile.IdKey, ct);
                }
                catch (Exception cleanupEx)
                {
                    logger.LogError(cleanupEx,
                        "Failed to cleanup uploaded file after exception: PhotoIdKey={PhotoIdKey}",
                        uploadedFile.IdKey);
                }
            }

            return BadRequest(new ProblemDetails
            {
                Title = "Upload failed",
                Detail = "An error occurred while uploading the photo",
                Status = StatusCodes.Status400BadRequest,
                Instance = HttpContext.Request.Path
            });
        }
    }
}
