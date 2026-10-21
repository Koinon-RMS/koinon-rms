using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Koinon.Api.Controllers;
using Koinon.Application.Common;
using Koinon.Application.DTOs;
using Koinon.Application.DTOs.Requests;
using Koinon.Application.Interfaces;
using Koinon.Domain.Data;
using Koinon.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Koinon.Api.Tests.Controllers;

public class PickupControllerTests
{
    private readonly Mock<IAuthorizedPickupService> _authorizedPickupServiceMock;
    private readonly Mock<ISupervisorModeService> _supervisorModeServiceMock;
    private readonly Mock<ILogger<PickupController>> _loggerMock;
    private readonly PickupController _controller;
    private readonly PickupController _supervisorController;

    // Valid IdKeys and IDs for testing
    private readonly string _attendanceIdKey = IdKeyHelper.Encode(100);
    private readonly string _childIdKey = IdKeyHelper.Encode(50);
    private readonly string _authorizedPickupIdKey = IdKeyHelper.Encode(200);
    private readonly int _regularUserId = 42;
    private readonly int _supervisorUserId = 99;

    public PickupControllerTests()
    {
        _authorizedPickupServiceMock = new Mock<IAuthorizedPickupService>();
        _supervisorModeServiceMock = new Mock<ISupervisorModeService>();
        _loggerMock = new Mock<ILogger<PickupController>>();

        // Setup controller with regular user (CheckInVolunteer role)
        _controller = CreateController();

        var regularClaims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, _regularUserId.ToString()),
            new Claim(ClaimTypes.Email, "volunteer@example.com"),
            new Claim(ClaimTypes.Name, "Jane Volunteer"),
            new Claim(ClaimTypes.Role, "CheckInVolunteer")
        };
        var regularIdentity = new ClaimsIdentity(regularClaims, "TestAuth");
        var regularPrincipal = new ClaimsPrincipal(regularIdentity);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = regularPrincipal
            }
        };

        // Setup controller with supervisor user
        _supervisorController = CreateController();

        var supervisorClaims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, _supervisorUserId.ToString()),
            new Claim(ClaimTypes.Email, "supervisor@example.com"),
            new Claim(ClaimTypes.Name, "John Supervisor"),
            new Claim(ClaimTypes.Role, "Supervisor")
        };
        var supervisorIdentity = new ClaimsIdentity(supervisorClaims, "TestAuth");
        var supervisorPrincipal = new ClaimsPrincipal(supervisorIdentity);

        _supervisorController.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = supervisorPrincipal
            }
        };
    }

    #region Kiosk authentication contract (issue #710)

    [Fact]
    public void VerifyPickup_IsKioskEndpoint_AnonymousPlusKioskAuthorize_NoRoleGate()
    {
        var method = typeof(PickupController).GetMethod(nameof(PickupController.VerifyPickup));

        method.Should().NotBeNull();
        method!.GetCustomAttribute<AllowAnonymousAttribute>()
            .Should().NotBeNull("kiosk verify-pickup must override the class-level [Authorize] like every other /checkin kiosk endpoint");
        method!.GetCustomAttribute<Koinon.Api.Attributes.KioskAuthorizeAttribute>()
            .Should().NotBeNull("verify-pickup authenticates with X-Kiosk-Token, not JWT roles");
        method!.GetCustomAttribute<AuthorizeAttribute>()
            .Should().BeNull("no JWT role gate may sit in front of the anonymous kiosk");
    }

    [Fact]
    public void RecordPickup_IsKioskEndpoint_AnonymousPlusKioskAuthorize_NoRoleGate()
    {
        var method = typeof(PickupController).GetMethod(nameof(PickupController.RecordPickup));

        method.Should().NotBeNull();
        method!.GetCustomAttribute<AllowAnonymousAttribute>()
            .Should().NotBeNull("kiosk record-pickup must override the class-level [Authorize]");
        method!.GetCustomAttribute<Koinon.Api.Attributes.KioskAuthorizeAttribute>()
            .Should().NotBeNull("record-pickup authenticates with X-Kiosk-Token, not JWT roles");
        method!.GetCustomAttribute<AuthorizeAttribute>()
            .Should().BeNull("no JWT role gate may sit in front of the anonymous kiosk");
    }

    [Fact]
    public void GetPickupHistory_StaysJwtSupervisorAdminEndpoint()
    {
        var method = typeof(PickupController).GetMethod(nameof(PickupController.GetPickupHistory));

        method.Should().NotBeNull();
        method!.GetCustomAttribute<AuthorizeAttribute>()
            .Should().NotBeNull()
            .And.Match<AuthorizeAttribute>(a => a.Roles == "Supervisor",
                "pickup history is the admin surface shipped in #709 and stays JWT-gated");
        method!.GetCustomAttribute<Koinon.Api.Attributes.KioskAuthorizeAttribute>()
            .Should().BeNull("history is not a kiosk endpoint");
    }

    [Fact]
    public async Task RecordPickup_WithoutOverride_DoesNotRequireSupervisorSession()
    {
        // Arrange — an authorized guardian checkout at a bare kiosk: no
        // X-Supervisor-Session, no JWT principal at all.
        var kioskController = CreateKioskController(sessionToken: null);
        var request = new RecordPickupRequest(
            AttendanceIdKey: _attendanceIdKey,
            PickupPersonIdKey: null,
            PickupPersonName: "Sarah Smith",
            WasAuthorized: true,
            AuthorizedPickupIdKey: _authorizedPickupIdKey,
            SupervisorOverride: false,
            SupervisorPersonIdKey: null,
            Notes: null);

        _authorizedPickupServiceMock
            .Setup(s => s.RecordPickupAsync(It.IsAny<RecordPickupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PickupLogDto>.Success(CreatePickupLog()));

        // Act
        var result = await kioskController.RecordPickup(request);

        // Assert — kiosk device auth alone is enough for an authorized pickup.
        var createdResult = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.Value.Should().NotBeNull();
        _supervisorModeServiceMock.Verify(
            s => s.ValidateSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "authorized pickups never consult the supervisor session store");
    }

    [Fact]
    public async Task RecordPickup_WithOverrideAndValidSession_DerivesSupervisorFromSession()
    {
        // Arrange — a supervisor override at the kiosk presents the session
        // header; the server derives the identity and ignores any
        // client-supplied SupervisorPersonIdKey (#710 ruling).
        var kioskController = CreateKioskController(sessionToken: "sess-token-1");
        var request = new RecordPickupRequest(
            AttendanceIdKey: _attendanceIdKey,
            PickupPersonIdKey: null,
            PickupPersonName: "Unknown Person",
            WasAuthorized: false,
            AuthorizedPickupIdKey: null,
            SupervisorOverride: true,
            SupervisorPersonIdKey: null, // client MUST NOT need to send this
            Notes: null);

        var sessionSupervisor = new SupervisorInfoDto
        {
            IdKey = IdKeyHelper.Encode(_supervisorUserId),
            FullName = "John Supervisor",
            FirstName = "John",
            LastName = "Supervisor"
        };
        _supervisorModeServiceMock
            .Setup(s => s.ValidateSessionAsync("sess-token-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(sessionSupervisor);

        RecordPickupRequest? captured = null;
        _authorizedPickupServiceMock
            .Setup(s => s.RecordPickupAsync(It.IsAny<RecordPickupRequest>(), It.IsAny<CancellationToken>()))
            .Callback<RecordPickupRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(Result<PickupLogDto>.Success(CreatePickupLog(supervisorOverride: true)));

        // Act
        var result = await kioskController.RecordPickup(request);

        // Assert
        var createdResult = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.Value.Should().NotBeNull();
        _supervisorModeServiceMock.Verify(
            s => s.ValidateSessionAsync("sess-token-1", It.IsAny<CancellationToken>()),
            Times.Once);
        captured.Should().NotBeNull();
        captured!.SupervisorPersonIdKey.Should().Be(sessionSupervisor.IdKey,
            "the override identity is derived from the validated session, never trusted from the client");
    }

    [Fact]
    public async Task RecordPickup_WithOverrideAndInvalidSession_Returns403()
    {
        // Arrange — no session header at all: override authority cannot be
        // established at a kiosk.
        var kioskController = CreateKioskController(sessionToken: null);
        var request = new RecordPickupRequest(
            AttendanceIdKey: _attendanceIdKey,
            PickupPersonIdKey: null,
            PickupPersonName: "Unknown Person",
            WasAuthorized: false,
            AuthorizedPickupIdKey: null,
            SupervisorOverride: true,
            SupervisorPersonIdKey: null,
            Notes: null);

        // Act
        var result = await kioskController.RecordPickup(request);

        // Assert
        var problemResult = result.Should().BeOfType<ObjectResult>().Subject;
        problemResult.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var problemDetails = problemResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problemDetails.Title.Should().Be("Forbidden");
    }

    [Fact]
    public async Task RecordPickup_WithOverrideAndExpiredSession_Returns403()
    {
        // Arrange
        var kioskController = CreateKioskController(sessionToken: "sess-token-expired");
        var request = new RecordPickupRequest(
            AttendanceIdKey: _attendanceIdKey,
            PickupPersonIdKey: null,
            PickupPersonName: "Unknown Person",
            WasAuthorized: false,
            AuthorizedPickupIdKey: null,
            SupervisorOverride: true,
            SupervisorPersonIdKey: null,
            Notes: null);

        _supervisorModeServiceMock
            .Setup(s => s.ValidateSessionAsync("sess-token-expired", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SupervisorInfoDto?)null);

        // Act
        var result = await kioskController.RecordPickup(request);

        // Assert
        var problemResult = result.Should().BeOfType<ObjectResult>().Subject;
        problemResult.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task VerifyPickup_ReachableByAnonymousKioskPrincipal()
    {
        // The kiosk has no JWT principal; the action itself must not consult
        // User roles. (Attribute pinning above proves the framework gate.)
        var kioskController = CreateKioskController(sessionToken: null);
        var request = new VerifyPickupRequest(
            AttendanceIdKey: _attendanceIdKey,
            PickupPersonIdKey: null,
            PickupPersonName: "Sarah Smith",
            SecurityCode: "4321");

        _authorizedPickupServiceMock
            .Setup(s => s.VerifyPickupAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PickupVerificationResultDto(
                IsAuthorized: true,
                AuthorizationLevel: AuthorizationLevel.Always,
                AuthorizedPickupIdKey: _authorizedPickupIdKey,
                Message: "Authorized to pick up child",
                RequiresSupervisorOverride: false));

        // Act
        var result = await kioskController.VerifyPickup(request);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().NotBeNull();
    }

    private PickupController CreateController()
    {
        var ctor = typeof(PickupController).GetConstructors().Single();
        var args = ctor.GetParameters().Select(p => p.ParameterType switch
        {
            var t when t == typeof(IAuthorizedPickupService) => (object)_authorizedPickupServiceMock.Object,
            var t when t == typeof(ISupervisorModeService) => _supervisorModeServiceMock.Object,
            var t when t == typeof(ILogger<PickupController>) => _loggerMock.Object,
            var t => throw new InvalidOperationException($"PickupControllerTests cannot supply constructor parameter of type {t.FullName}"),
        }).ToArray();
        return (PickupController)ctor.Invoke(args);
    }

    private PickupController CreateKioskController(string? sessionToken)
    {
        var controller = CreateController();

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity()), // anonymous kiosk
        };
        if (sessionToken is not null)
        {
            httpContext.Request.Headers["X-Supervisor-Session"] = sessionToken;
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        return controller;
    }

    private PickupLogDto CreatePickupLog(bool supervisorOverride = false) =>
        new(
            IdKey: IdKeyHelper.Encode(300),
            AttendanceIdKey: _attendanceIdKey,
            ChildName: "Johnny Smith",
            PickupPersonName: "Sarah Smith",
            WasAuthorized: !supervisorOverride,
            SupervisorOverride: supervisorOverride,
            SupervisorName: supervisorOverride ? "John Supervisor" : null,
            CheckoutDateTime: DateTime.UtcNow,
            Notes: null);

    #endregion

    #region VerifyPickup behavior

    [Fact]
    public async Task VerifyPickup_WithValidRequest_ReturnsOkWithResult()
    {
        // Arrange
        var request = new VerifyPickupRequest(
            AttendanceIdKey: _attendanceIdKey,
            PickupPersonIdKey: IdKeyHelper.Encode(75),
            PickupPersonName: "Sarah Smith",
            SecurityCode: "1234");

        var expectedResult = new PickupVerificationResultDto(
            IsAuthorized: true,
            AuthorizationLevel: AuthorizationLevel.Always,
            AuthorizedPickupIdKey: _authorizedPickupIdKey,
            Message: "Authorized to pick up child",
            RequiresSupervisorOverride: false);

        _authorizedPickupServiceMock
            .Setup(s => s.VerifyPickupAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _controller.VerifyPickup(request);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value!;
        var dataProperty = response.GetType().GetProperty("data");
        dataProperty.Should().NotBeNull("response should have a 'data' property");
        var verificationResult = dataProperty!.GetValue(response).Should().BeOfType<PickupVerificationResultDto>().Subject;
        verificationResult.IsAuthorized.Should().BeTrue();
        verificationResult.AuthorizationLevel.Should().Be(AuthorizationLevel.Always);
        verificationResult.RequiresSupervisorOverride.Should().BeFalse();
        verificationResult.Message.Should().Be("Authorized to pick up child");
    }

    [Fact]
    public async Task VerifyPickup_WithMissingAttendanceIdKey_ReturnsBadRequest()
    {
        // Arrange
        var request = new VerifyPickupRequest(
            AttendanceIdKey: "",
            PickupPersonIdKey: IdKeyHelper.Encode(75),
            PickupPersonName: "Sarah Smith",
            SecurityCode: "1234");

        // Act
        var result = await _controller.VerifyPickup(request);

        // Assert
        var badRequestResult = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var problemDetails = badRequestResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problemDetails.Title.Should().Be("Invalid request");
        problemDetails.Detail.Should().Contain("AttendanceIdKey is required");
    }

    [Fact]
    public async Task VerifyPickup_WithMissingSecurityCode_ReturnsBadRequest()
    {
        // Arrange
        var request = new VerifyPickupRequest(
            AttendanceIdKey: _attendanceIdKey,
            PickupPersonIdKey: IdKeyHelper.Encode(75),
            PickupPersonName: "Sarah Smith",
            SecurityCode: "");

        // Act
        var result = await _controller.VerifyPickup(request);

        // Assert
        var badRequestResult = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var problemDetails = badRequestResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problemDetails.Title.Should().Be("Invalid request");
        problemDetails.Detail.Should().Contain("SecurityCode is required");
    }

    [Fact]
    public async Task VerifyPickup_WithUnauthorizedPerson_ReturnsOkWithUnauthorizedResult()
    {
        // Arrange
        var request = new VerifyPickupRequest(
            AttendanceIdKey: _attendanceIdKey,
            PickupPersonIdKey: IdKeyHelper.Encode(75),
            PickupPersonName: "Unknown Person",
            SecurityCode: "9999");

        var expectedResult = new PickupVerificationResultDto(
            IsAuthorized: false,
            AuthorizationLevel: null,
            AuthorizedPickupIdKey: null,
            Message: "Person is not authorized to pick up this child",
            RequiresSupervisorOverride: true);

        _authorizedPickupServiceMock
            .Setup(s => s.VerifyPickupAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _controller.VerifyPickup(request);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value!;
        var dataProperty = response.GetType().GetProperty("data");
        dataProperty.Should().NotBeNull("response should have a 'data' property");
        var verificationResult = dataProperty!.GetValue(response).Should().BeOfType<PickupVerificationResultDto>().Subject;
        verificationResult.IsAuthorized.Should().BeFalse();
        verificationResult.RequiresSupervisorOverride.Should().BeTrue();
    }

    #endregion

    #region RecordPickup behavior

    [Fact]
    public async Task RecordPickup_WithValidRequest_ReturnsCreated()
    {
        // Arrange
        var request = new RecordPickupRequest(
            AttendanceIdKey: _attendanceIdKey,
            PickupPersonIdKey: IdKeyHelper.Encode(75),
            PickupPersonName: "Sarah Smith",
            WasAuthorized: true,
            AuthorizedPickupIdKey: _authorizedPickupIdKey,
            SupervisorOverride: false,
            SupervisorPersonIdKey: null,
            Notes: null);

        _authorizedPickupServiceMock
            .Setup(s => s.RecordPickupAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PickupLogDto>.Success(CreatePickupLog()));

        // Act
        var result = await _controller.RecordPickup(request);

        // Assert
        var createdResult = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var response = createdResult.Value!;
        var dataProperty = response.GetType().GetProperty("data");
        dataProperty.Should().NotBeNull("response should have a 'data' property");
        var pickupLog = dataProperty!.GetValue(response).Should().BeOfType<PickupLogDto>().Subject;
        pickupLog.WasAuthorized.Should().BeTrue();
        pickupLog.SupervisorOverride.Should().BeFalse();
        pickupLog.PickupPersonName.Should().Be("Sarah Smith");
    }

    [Fact]
    public async Task RecordPickup_WithMissingAttendanceIdKey_ReturnsBadRequest()
    {
        // Arrange
        var request = new RecordPickupRequest(
            AttendanceIdKey: "",
            PickupPersonIdKey: IdKeyHelper.Encode(75),
            PickupPersonName: "Sarah Smith",
            WasAuthorized: true,
            AuthorizedPickupIdKey: _authorizedPickupIdKey,
            SupervisorOverride: false,
            SupervisorPersonIdKey: null,
            Notes: null);

        // Act
        var result = await _controller.RecordPickup(request);

        // Assert
        var badRequestResult = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var problemDetails = badRequestResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problemDetails.Title.Should().Be("Invalid request");
        problemDetails.Detail.Should().Contain("AttendanceIdKey is required");
    }

    [Fact]
    public async Task RecordPickup_WhenServerVerificationRefusesTheClaim_Returns422()
    {
        // Arrange — the service's own re-verification refuses the claim (#710)
        var kioskController = CreateKioskController(sessionToken: null);
        var request = new RecordPickupRequest(
            AttendanceIdKey: _attendanceIdKey,
            PickupPersonIdKey: null,
            PickupPersonName: "Anyone At All",
            WasAuthorized: true,
            AuthorizedPickupIdKey: null,
            SupervisorOverride: false,
            SupervisorPersonIdKey: null,
            Notes: null);

        _authorizedPickupServiceMock
            .Setup(s => s.RecordPickupAsync(It.IsAny<RecordPickupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PickupLogDto>.Failure(Error.UnprocessableEntity(
                "The pickup person is not authorized for this child; the pickup cannot be recorded as authorized.")));

        // Act
        var result = await kioskController.RecordPickup(request);

        // Assert — a refused pickup is a 422 ProblemDetails, not a 500
        var unprocessable = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        var problemDetails = unprocessable.Value.Should().BeOfType<ProblemDetails>().Subject;
        problemDetails.Status.Should().Be(StatusCodes.Status422UnprocessableEntity);
        problemDetails.Title.Should().Be("UNPROCESSABLE_ENTITY");
        problemDetails.Detail.Should().Contain("not authorized for this child");
    }

    [Fact]
    public async Task RecordPickup_WhenServiceReportsAValidationError_Returns400()
    {
        // Arrange
        var kioskController = CreateKioskController(sessionToken: null);
        var request = new RecordPickupRequest(
            AttendanceIdKey: _attendanceIdKey,
            PickupPersonIdKey: null,
            PickupPersonName: "Sarah Smith",
            WasAuthorized: false,
            AuthorizedPickupIdKey: null,
            SupervisorOverride: false,
            SupervisorPersonIdKey: null,
            Notes: null);

        _authorizedPickupServiceMock
            .Setup(s => s.RecordPickupAsync(It.IsAny<RecordPickupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PickupLogDto>.Failure(Error.Validation(
                "SupervisorOverride is required when WasAuthorized is false")));

        // Act
        var result = await kioskController.RecordPickup(request);

        // Assert
        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var problemDetails = badRequest.Value.Should().BeOfType<ProblemDetails>().Subject;
        problemDetails.Title.Should().Be("Invalid request");
        problemDetails.Detail.Should().Contain("SupervisorOverride is required");
    }

    [Fact]
    public async Task RecordPickup_WhenAttendanceIsNotFound_Returns404()
    {
        // Arrange
        var kioskController = CreateKioskController(sessionToken: null);
        var request = new RecordPickupRequest(
            AttendanceIdKey: _attendanceIdKey,
            PickupPersonIdKey: null,
            PickupPersonName: "Sarah Smith",
            WasAuthorized: true,
            AuthorizedPickupIdKey: null,
            SupervisorOverride: false,
            SupervisorPersonIdKey: null,
            Notes: null);

        _authorizedPickupServiceMock
            .Setup(s => s.RecordPickupAsync(It.IsAny<RecordPickupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PickupLogDto>.Failure(Error.NotFound("Attendance", _attendanceIdKey)));

        // Act
        var result = await kioskController.RecordPickup(request);

        // Assert
        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        var problemDetails = notFound.Value.Should().BeOfType<ProblemDetails>().Subject;
        problemDetails.Title.Should().Be("Attendance not found");
    }

    #endregion

    #region GetPickupHistory Tests

    [Fact]
    public async Task GetPickupHistory_WithValidChildIdKey_ReturnsOkWithHistory()
    {
        // Arrange
        var expectedHistory = new List<PickupLogDto>
        {
            new(
                IdKey: IdKeyHelper.Encode(300),
                AttendanceIdKey: _attendanceIdKey,
                ChildName: "Johnny Smith",
                PickupPersonName: "Sarah Smith",
                WasAuthorized: true,
                SupervisorOverride: false,
                SupervisorName: null,
                CheckoutDateTime: DateTime.UtcNow.AddDays(-1),
                Notes: null),
            new(
                IdKey: IdKeyHelper.Encode(301),
                AttendanceIdKey: IdKeyHelper.Encode(101),
                ChildName: "Johnny Smith",
                PickupPersonName: "Mike Smith",
                WasAuthorized: true,
                SupervisorOverride: false,
                SupervisorName: null,
                CheckoutDateTime: DateTime.UtcNow.AddDays(-7),
                Notes: null)
        };

        _authorizedPickupServiceMock
            .Setup(s => s.GetPickupHistoryAsync(
                _childIdKey,
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedHistory);

        // Act
        var result = await _supervisorController.GetPickupHistory(_childIdKey, null, null);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value!;
        var dataProperty = response.GetType().GetProperty("data");
        dataProperty.Should().NotBeNull("response should have a 'data' property");
        var history = dataProperty!.GetValue(response).Should().BeAssignableTo<IEnumerable<PickupLogDto>>().Subject.ToList();
        history.Should().HaveCount(2);
        history[0].ChildName.Should().Be("Johnny Smith");
        history[1].PickupPersonName.Should().Be("Mike Smith");
    }

    [Fact]
    public async Task GetPickupHistory_WithInvalidDateRange_ReturnsBadRequest()
    {
        // Arrange
        var fromDate = new DateTime(2024, 1, 15);
        var toDate = new DateTime(2024, 1, 10); // toDate before fromDate

        // Act
        var result = await _supervisorController.GetPickupHistory(_childIdKey, fromDate, toDate);

        // Assert
        var badRequestResult = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var problemDetails = badRequestResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problemDetails.Title.Should().Be("Invalid date range");
        problemDetails.Detail.Should().Contain("toDate' must be greater than or equal to 'fromDate");
    }

    [Fact]
    public async Task GetPickupHistory_WithDateRange_ReturnsFilteredHistory()
    {
        // Arrange
        var fromDate = new DateTime(2024, 1, 1);
        var toDate = new DateTime(2024, 1, 31);

        var expectedHistory = new List<PickupLogDto>
        {
            new(
                IdKey: IdKeyHelper.Encode(300),
                AttendanceIdKey: _attendanceIdKey,
                ChildName: "Johnny Smith",
                PickupPersonName: "Sarah Smith",
                WasAuthorized: true,
                SupervisorOverride: false,
                SupervisorName: null,
                CheckoutDateTime: new DateTime(2024, 1, 15, 12, 0, 0),
                Notes: null)
        };

        _authorizedPickupServiceMock
            .Setup(s => s.GetPickupHistoryAsync(
                _childIdKey,
                fromDate,
                toDate,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedHistory);

        // Act
        var result = await _supervisorController.GetPickupHistory(_childIdKey, fromDate, toDate);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value!;
        var dataProperty = response.GetType().GetProperty("data");
        dataProperty.Should().NotBeNull("response should have a 'data' property");
        var history = dataProperty!.GetValue(response).Should().BeAssignableTo<IEnumerable<PickupLogDto>>().Subject.ToList();
        history.Should().HaveCount(1);
        history[0].CheckoutDateTime.Should().BeAfter(fromDate).And.BeBefore(toDate);
    }

    [Fact]
    public async Task GetPickupHistory_WithNoHistory_ReturnsEmptyList()
    {
        // Arrange
        var expectedHistory = new List<PickupLogDto>();

        _authorizedPickupServiceMock
            .Setup(s => s.GetPickupHistoryAsync(
                _childIdKey,
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedHistory);

        // Act
        var result = await _supervisorController.GetPickupHistory(_childIdKey, null, null);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value!;
        var dataProperty = response.GetType().GetProperty("data");
        dataProperty.Should().NotBeNull("response should have a 'data' property");
        var history = dataProperty!.GetValue(response).Should().BeAssignableTo<IEnumerable<PickupLogDto>>().Subject.ToList();
        history.Should().BeEmpty();
    }

    #endregion
}
