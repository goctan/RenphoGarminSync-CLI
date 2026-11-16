
using RenphoGarminSync.Console;
using RenphoGarminSync.Console.Models;
using RenphoGarminSync.Console.Persistence;
using RenphoGarminSync.ConsoleApp.Persistence;
using RenphoGarminSync.Garmin.Api;
using RenphoGarminSync.Garmin.Auth;
using RenphoGarminSync.Garmin.Auth.Authenticators;
using RenphoGarminSync.Garmin.Auth.Persistence;
using RenphoGarminSync.Garmin.Shared.Exceptions;
using RenphoGarminSync.Renpho.Api;
using RenphoGarminSync.Renpho.Auth;
using RenphoGarminSync.Renpho.Auth.Authenticators;
using RenphoGarminSync.Renpho.Auth.Persistence;
using RenphoGarminSync.Renpho.Shared.Exceptions;
using RenphoGarminSync.Renpho.Shared.Extensions;
using RenphoGarminSync.Renpho.Shared.Models.Responses;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RenphoGarminSync.ConsoleApp
{
    public class RenphoGarminSyncCommandHandler
    {
        private readonly IGarminApi _garminApi;
        private readonly IRenphoApi _renphoApi;
        private readonly IHandledMeasurementsRepository _measurementsRepository;

        private readonly Random _random = new Random();
        private readonly string _renphoProfile;
        private readonly bool _dryRun;
        private readonly bool _noFitFiles;
        private AppConfig _config;

        public RenphoGarminSyncCommandHandler(string garminUsername, string garminPassword, string renphoUsername, string renphoPassword, string renphoProfile, bool dryRun, bool noFitFiles)
        {
            _config = ConfigurationHelper.GetApplicationConfig();

            var garminAuthCache = new GarminFileBasedAuthCache(_config.General.CachePath);
            var garminAuthApi = new GarminAuthApi(_config.Garmin);
            var garminAuthService = new GarminAuthService(garminUsername, garminPassword, garminAuthCache, garminAuthApi);

            var authenticator = new GarminApiAuthenticator(garminAuthService);
            _garminApi = new GarminApi(_config.Garmin, authenticator);

            var renphoAuthCache = new RenphoInMemoryAuthCache();
            var renphoAuthApi = new RenphoAuthApi(_config.Renpho);
            var renphoAuthService = new RenphoAuthService(renphoUsername, renphoPassword, renphoAuthCache, renphoAuthApi);
            var renphoAuthenticator = new RenphoApiAuthenticator(renphoAuthService, _config.Renpho);
            _renphoApi = new RenphoApi(_config.Renpho.EncryptionSecret, renphoAuthenticator);

            _measurementsRepository = new CSVHandledMeasurementsRepository(_config.General.CachePath);
            _renphoProfile = renphoProfile;
            _dryRun = dryRun;
            _noFitFiles = noFitFiles;
        }

        public async Task<int> InvokeAsync()
        {
            try
            {
                var renphoUserId = await GetRenphoApiUserIdAsync(_renphoProfile);
                var measurements = await GetMeasurementsForUserAsync(renphoUserId);

                if (!measurements.Any())
                {
                    AnsiConsole.MarkupLine("[maroon]No measurements were found for current Renpho user.[/]");
                    return 0;
                }

                var measurementIds = measurements.Select(x => x.Id.Value);
                var newMeasurementIds = (await _measurementsRepository.FilterOutHandledMeasurementsAsync(renphoUserId, measurementIds)).ToHashSet();

                if (!newMeasurementIds.Any())
                {
                    AnsiConsole.MarkupLine("[green]No new measurements have been detected.[/]");
                    DrawMeasurementsTable("Last Measurements", measurements.Take(10));
                    return 0;
                }

                var newMeasurements = measurements.Where(x => newMeasurementIds.Contains(x.Id.Value));
                DrawMeasurementsTable("New Measurements", newMeasurements);

                if (_dryRun)
                {
                    var diagnosticMeasurements = newMeasurements.ToList();

                    System.Console.WriteLine("[FIT DIAGNOSTIC] Données disponibles :");
                    System.Console.WriteLine($"Graisse corporelle : {diagnosticMeasurements.Count(x => x.BodyFat.HasValue)} / {diagnosticMeasurements.Count}");
                    System.Console.WriteLine($"Eau corporelle : {diagnosticMeasurements.Count(x => x.Water.HasValue)} / {diagnosticMeasurements.Count}");
                    System.Console.WriteLine($"Masse musculaire : {diagnosticMeasurements.Count(x => x.Muscle.HasValue)} / {diagnosticMeasurements.Count}");
                    System.Console.WriteLine($"Masse osseuse : {diagnosticMeasurements.Count(x => x.Bone.HasValue)} / {diagnosticMeasurements.Count}");

                    foreach (var measurement in diagnosticMeasurements.Take(3))
                    {
                        var fitContent = FitGenerator.GenerateMeasurementFitFile(measurement);
                        System.Console.WriteLine(
                            $"[FIT DIAGNOSTIC] Pesée {measurement.Id} : fichier FIT généré ({fitContent.Length} octets).");
                    }

                    AnsiConsole.MarkupLine("[green]Dry run - Aucun envoi vers Garmin.[/]");
                    return 0;
                }

                foreach (var measurement in newMeasurements)
                {
                    var fitContent = FitGenerator.GenerateMeasurementFitFile(measurement);
                    var fileName = $"Renpho_{measurement.Id}.fit";

                    if (!_noFitFiles)
                        await SaveFitFile(measurement, fitContent, fileName);

                    var result = await _garminApi.UploadActivity(fileName, fitContent);

                    if (result?.DetailedImportResult?.Failures?.Any() == true)
                    {
                        AnsiConsole.MarkupLineInterpolated($"[maroon]Failed during garmin activity upload, file: {fileName}, skipping processing.[/]");

                        foreach (var failure in result?.DetailedImportResult?.Failures)
                            foreach (var message in failure.Messages)
                                AnsiConsole.MarkupLineInterpolated($"\t[maroon]Code: {message.Code}, Content: {message.Content}[/]");

                        await Task.Delay(_random.Next(1000, 1500));
                        continue;
                    }

                    await _measurementsRepository.MarkMeasurementAsHandledAsync(renphoUserId, measurement.Id.Value, measurement.TimeStamp.Value);
                    AnsiConsole.MarkupLineInterpolated($"[green]Uploaded Garmin FIT file, name: {fileName}[/]");

                    await Task.Delay(_random.Next(1000, 1500));
                }

                return 0;
            }
            catch (GarminException ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[maroon]Garmin Exception encountered, code: [yellow]{ex.StatusCode}[/], message: [yellow]{ex.Message}[/][/]");
                return 1;
            }
            catch (RenphoException ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[maroon]Renpho Exception encountered, code: [yellow]{ex.StatusCode}[/], message: [yellow]{ex.Message}[/][/]");
                return 1;
            }
        }

        private static void DrawMeasurementsTable(string tableHeader, IEnumerable<BodyScaleResponse> measurements)
        {
            if (!measurements.Any())
                return;

            var table = new Table();
            table.Title = new TableTitle(tableHeader);

            table.AddColumn("Date");
            table.AddColumn("Weight");
            table.AddColumn("BMI");
            table.AddColumn("ID");

            foreach (var measurement in measurements)
            {
                var measurementTime = DateTimeOffset.FromUnixTimeSeconds(measurement.TimeStamp.Value);
                var timeString = measurementTime.ToString("yyyy-MM-dd HH:ss");

                table.AddRow(timeString, $"{measurement.Weight:F1}", $"{measurement.BMI:F1}", $"{measurement.Id}");
            }

            AnsiConsole.Write(table);
        }

        private async Task SaveFitFile(BodyScaleResponse measurement, byte[] fitContent, string fileName)
        {
            var fitFilesDir = Path.Combine(_config.General.CachePath, "FitFiles");
            Directory.CreateDirectory(fitFilesDir);
            var filePath = Path.Combine(fitFilesDir, fileName);
            await File.WriteAllBytesAsync(filePath, fitContent);
        }

        private async Task<IEnumerable<BodyScaleResponse>> GetMeasurementsForUserAsync(long userId)
        {
            System.Console.WriteLine("[RENPHO DIAGNOSTIC] Starting measurement retrieval.");

            var deviceInfo = await _renphoApi.GetDeviceInfoAsync();

            if (deviceInfo is null)
            {
                System.Console.WriteLine("[RENPHO DIAGNOSTIC] Device info response is NULL.");
                return [];
            }

            if (deviceInfo.Scale is null || !deviceInfo.Scale.Any())
            {
                System.Console.WriteLine("[RENPHO DIAGNOSTIC] No scales returned by device/count.");
                return [];
            }

            System.Console.WriteLine($"[RENPHO DIAGNOSTIC] Number of scale entries: {deviceInfo.Scale.Length}");

            var outWeightIns = new List<BodyScaleResponse>();

            foreach (var scaleInfo in deviceInfo.Scale)
            {
                var associatedWithUser = scaleInfo.UserIds?.Contains(userId) == true;

                System.Console.WriteLine(
                    $"[RENPHO DIAGNOSTIC] Scale: count={scaleInfo.Count}, " +
                    $"associatedWithUser={associatedWithUser}");

                if (!associatedWithUser)
                {
                    System.Console.WriteLine("[RENPHO DIAGNOSTIC] Skipping scale: user does not match.");
                    continue;
                }

                var measurements = await _renphoApi.GetBodyScaleMeasurements(
                    userId,
                    scaleInfo.Count,
                    scaleInfo.TableName);

                if (measurements is null)
                {
                    System.Console.WriteLine("[RENPHO DIAGNOSTIC] Measurement query returned NULL.");
                    continue;
                }

                var rawMeasurements = measurements.ToList();

                var matchingUserMeasurements = rawMeasurements
                    .Where(x => x.UserId == userId)
                    .ToList();

                var bodyScaleMeasurements = matchingUserMeasurements
                    .Where(x => x.DeviceType.GetDeviceTypeForTag().IsBodyWeightScale())
                    .ToList();

                System.Console.WriteLine(
                    $"[RENPHO DIAGNOSTIC] Raw measurements: {rawMeasurements.Count}");

                System.Console.WriteLine(
                    $"[RENPHO DIAGNOSTIC] Matching user: {matchingUserMeasurements.Count}");

                System.Console.WriteLine(
                    $"[RENPHO DIAGNOSTIC] Recognized body-scale measurements: {bodyScaleMeasurements.Count}");

                foreach (var deviceType in rawMeasurements
                    .Select(x => x.DeviceType ?? "(null)")
                    .Distinct())
                {
                    System.Console.WriteLine(
                        $"[RENPHO DIAGNOSTIC] Device type: {deviceType}");
                }

                outWeightIns.AddRange(bodyScaleMeasurements);
            }

            System.Console.WriteLine(
                $"[RENPHO DIAGNOSTIC] Total retained measurements: {outWeightIns.Count}");

            return outWeightIns.OrderByDescending(x => x.TimeStamp).ToList();
        }

        private async Task<long> GetRenphoApiUserIdAsync(string targetUserName)
        {
            if (string.IsNullOrWhiteSpace(targetUserName))
                return await _renphoApi.GetLoggedInUserIdAsync();

            var loggedInUserInfo = await _renphoApi.GetLoggedInUserInfoAsync();

            if (loggedInUserInfo.AccountName == targetUserName)
                return loggedInUserInfo.Id;

            var guestInfos = await _renphoApi.QueryFamilyMembers();

            if (!guestInfos.Any())
                throw new InvalidOperationException("Logged in user doesn't match the requested name, and no family members were found");

            var matchingInfo = guestInfos.FirstOrDefault(x => x.AccountName == targetUserName);

            if (matchingInfo is null)
                throw new InvalidOperationException("No Renpho user found matching the requested name");

            return matchingInfo.Id;
        }
    }
}
