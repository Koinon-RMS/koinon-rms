using FluentValidation;
using Koinon.Application.DTOs.Auth;
using Koinon.Application.Interfaces;
using Koinon.Application.Interfaces.Reporting;
using Koinon.Application.Services;
using Koinon.Application.Services.Common;
using Koinon.Application.Services.Exports;
using Koinon.Application.Services.Reporting;
using Koinon.Application.Services.Reporting.DataProviders;
using Koinon.Application.Validators;
using Microsoft.Extensions.DependencyInjection;
using QuestPDF.Infrastructure;

namespace Koinon.Application.Extensions;

/// <summary>
/// Extension methods for IServiceCollection to configure Koinon application services.
/// Provides dependency injection registration for application-layer services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds all Koinon application services to the service collection.
    /// Registers services for person management, groups, families, and check-in operations.
    /// NOTE: IUserContext must be registered by the API/Infrastructure layer before calling this method.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddKoinonApplicationServices(
        this IServiceCollection services)
    {
        // NOTE: IUserContext is required by services but must be registered by the calling layer
        // (API or Infrastructure) before this method is called. This is intentional to keep
        // the Application layer independent of HTTP context or authentication implementation.

        // Authentication service
        services.AddScoped<IAuthService, AuthService>();

        // Security services
        services.AddScoped<ISecurityClaimService, SecurityClaimService>();
        services.AddScoped<ISecurityRoleService, SecurityRoleService>();

        // Core entity services
        services.AddScoped<IPersonService, PersonService>();
        services.AddScoped<IGroupService, GroupService>();
        services.AddScoped<IGroupTypeService, GroupTypeService>();
        services.AddScoped<IGroupMemberRequestService, GroupMemberRequestService>();
        services.AddScoped<IMyGroupsService, MyGroupsService>();
        services.AddScoped<IFamilyService, FamilyService>();
        services.AddScoped<ICampusService, CampusService>();
        services.AddScoped<IDefinedTypeService, DefinedTypeService>();
        services.AddScoped<ILocationService, LocationService>();
        services.AddScoped<IScheduleService, ScheduleService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IPublicGroupService, PublicGroupService>();

        // Check-in common services (foundation classes for consistent patterns)
        services.AddScoped<CheckinDataLoader>();
        services.AddScoped<ConcurrentOperationHelper>();
        services.AddScoped<GivingDataLoader>();
        services.AddScoped<GroupDataLoader>();
        services.AddScoped<NotificationDataLoader>();
        services.AddScoped<PagerDataLoader>();

        // Grade calculation service
        services.AddScoped<IGradeCalculationService, GradeCalculationService>();

        // Check-in services
        services.AddScoped<ICheckinConfigurationService, CheckinConfigurationService>();
        services.AddScoped<ICheckinSearchService, CheckinSearchService>();
        services.AddScoped<ICheckinAttendanceService, CheckinAttendanceService>();
        services.AddScoped<ILabelGenerationService, LabelGenerationService>();
        services.AddScoped<ICapacityService, CapacityService>();

        // Kiosk registration service
        services.AddScoped<ICheckinRegistrationService, CheckinRegistrationService>();

        // Device/Kiosk management service
        services.AddScoped<IDeviceService, DeviceService>();

        // Device/Kiosk validation service
        services.AddScoped<IDeviceValidationService, DeviceValidationService>();

        // Supervisor mode service
        services.AddScoped<ISupervisorModeService, SupervisorModeService>();

        // Analytics services
        services.AddScoped<IAttendanceAnalyticsService, AttendanceAnalyticsService>();
        services.AddScoped<IAttendanceTakerService, AttendanceTakerService>();

        // First-time visitor and follow-up services
        services.AddScoped<IFirstTimeVisitorService, FirstTimeVisitorService>();
        services.AddScoped<IFollowUpService, FollowUpService>();
        services.AddScoped<IFollowUpRetryService, FollowUpRetryService>();

        // Parent paging service
        services.AddScoped<IParentPagingService, ParentPagingService>();

        // Authorized pickup service
        services.AddScoped<IAuthorizedPickupService, AuthorizedPickupService>();

        // Room roster service
        services.AddScoped<IRoomRosterService, RoomRosterService>();

        // Check-in operations dashboard (#482) — live room counts + open/close toggle
        services.AddScoped<ICheckinOperationsService, CheckinOperationsService>();

        // Communication services
        services.AddScoped<ICommunicationService, CommunicationService>();
        services.AddScoped<ICommunicationTemplateService, CommunicationTemplateService>();
        services.AddScoped<ICommunicationPreferenceService, CommunicationPreferenceService>();
        services.AddScoped<ICommunicationSender, CommunicationSender>();
        services.AddScoped<IMergeFieldService, MergeFieldService>();
        services.AddScoped<IScheduledCommunicationProcessor, ScheduledCommunicationProcessor>();

        // Person merge and duplicate detection services
        services.AddScoped<IDuplicateDetectionService, DuplicateDetectionService>();
        services.AddScoped<IPersonMergeService, PersonMergeService>();
        services.AddScoped<IDuplicateIgnoreService, DuplicateIgnoreService>();

        // Global search service
        services.AddScoped<IGlobalSearchService, GlobalSearchService>();

        // Self-service profile service
        services.AddScoped<IMyProfileService, MyProfileService>();

        // Notification service
        services.AddScoped<INotificationService, NotificationService>();

        // User settings service
        services.AddScoped<IUserSettingsService, UserSettingsService>();

        // File management service
        services.AddScoped<IFileService, FileService>();

        // Giving/Financial services
        services.AddScoped<IContributionStatementService, ContributionStatementService>();
        services.AddScoped<IBatchDonationEntryService, BatchDonationEntryService>();
        services.AddScoped<IFundService, FundService>();
        services.AddScoped<StatementPdfGenerator>();

        // Audit services
        services.AddScoped<IAuditService, AuditService>();

        // Audit log cleanup service (called by Hangfire)
        services.AddScoped<AuditLogCleanupService>();

        // Reporting services
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IReportScheduleService, ReportScheduleService>();
        services.AddScoped<ReportGeneratorFactory>();

        // Report generators (Transient for stateless generation)
        services.AddTransient<AttendanceSummaryReportGenerator>();
        services.AddTransient<GivingSummaryReportGenerator>();
        services.AddTransient<DirectoryReportGenerator>();

        // Report Data Providers
        services.AddScoped<ReportDataProviderFactory>();
        services.AddScoped<IReportDataProvider, AttendanceSummaryDataProvider>();
        services.AddScoped<IReportDataProvider, GivingSummaryDataProvider>();
        services.AddScoped<IReportDataProvider, DirectoryDataProvider>();
        services.AddScoped<IReportDataProvider, CustomSqlDataProvider>();

        // Data Export Services
        services.AddScoped<IDataExportService, DataExportService>();

        // Export Data Providers
        services.AddScoped<IExportDataProvider, PersonExportProvider>();
        services.AddScoped<IExportDataProvider, FamilyExportProvider>();
        services.AddScoped<IExportDataProvider, GroupExportProvider>();
        services.AddScoped<IExportDataProvider, ContributionExportProvider>();

        // Export Format Generators
        services.AddScoped<IExportFormatGenerator, CsvExportGenerator>();
        services.AddScoped<IExportFormatGenerator, ExcelExportGenerator>();

        // Data Import Services (#696)
        services.AddScoped<ICsvParserService, CsvParserService>();
        services.AddScoped<IDataImportService, DataImportService>();

        // Configure QuestPDF license
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        // AutoMapper 13+ absorbed the DI extensions package; assembly registration
        // moved behind the configuration lambda (AddMaps).
        services.AddAutoMapper(cfg => cfg.AddMaps(typeof(ServiceCollectionExtensions).Assembly));

        // FluentValidation - register all validators
        services.AddValidatorsFromAssembly(typeof(ServiceCollectionExtensions).Assembly);

        // Future: Add MediatR when needed
        // services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ServiceCollectionExtensions).Assembly));

        return services;
    }
}
