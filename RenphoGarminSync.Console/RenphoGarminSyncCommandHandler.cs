
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
        private readonly AppConfig _config;

        // 17 November 2026 at 00:00 in France (UTC+1).
        private static readonly DateTimeOffset SyncStartDate =
            new DateTimeOffset(2026, 11, 16, 18, 0, 0, TimeSpan.Zero);

        public RenphoGarminSyncCommandHandler(
            string garminUsername,
            string garminPassword,
            string renphoUsername,
            string renphoPassword,
            string renphoProfile)
        {
            _config = ConfigurationHelper.GetApplicationConfig();

            var garminAuthCache =
                new GarminFileBasedAuthCache(_config.General.CachePath);

            var garminAuthApi =
                new GarminAuthApi(_config.Garmin);

            var garminAuthService =
                new GarminAuthService(
                    garminUsername,
                    garminPassword,
                    garminAuthCache,
                    garminAuthApi);

            var authenticator =
                new GarminApiAuthenticator(garminAuthService);

            _garminApi =
                new GarminApi(_config.Garmin, authenticator);

            var renphoAuthCache =
                new RenphoInMemoryAuthCache();

            var renphoAuthApi =
                new RenphoAuthApi(_config.Renpho);

            var renphoAuthService =
                new RenphoAuthService(
                    renphoUsername,
                    renphoPassword,
                    renphoAuthCache,
                    renphoAuthApi);

            var renphoAuthenticator =
                new RenphoApiAuthenticator(
                    renphoAuthService,
                    _config.Renpho);

            _renphoApi =
                new RenphoApi(
                    _config.Renpho.EncryptionSecret,
                    renphoAuthenticator);

            _measurementsRepository =
                new CSVHandledMeasurementsRepository(
                    _config.General.CachePath);

            _renphoProfile = renphoProfile;
        }

        public async Task<int> InvokeAsync()
        {
            try
            {
                var renphoUserId =
                    await GetRenphoApiUserIdAsync(_renphoProfile);

                var measurements =
                    (await GetMeasurementsForUserAsync(renphoUserId))
                    .ToList();

                if (!measurements.Any())
                {
                    AnsiConsole.MarkupLine(
                        "[yellow]Aucune pesée RENPHO trouvée.[/]");
                    return 0;
                }

                // Keep only measurements from 17 November 2026 onwards.
                var eligibleMeasurements = measurements
                    .Where(x =>
                        x.TimeStamp.HasValue &&
                        DateTimeOffset.FromUnixTimeSeconds(
                            x.TimeStamp.Value) >= SyncStartDate)
                    .ToList();

                System.Console.WriteLine(
                    $"[SYNC] Pesées RENPHO récupérées : {measurements.Count}");

                System.Console.WriteLine(
                    $"[SYNC] Pesées à partir du 17/11/2026 : {eligibleMeasurements.Count}");

                if (!eligibleMeasurements.Any())
                {
                    AnsiConsole.MarkupLine(
                        "[green]Aucune pesée à synchroniser après la date de départ.[/]");
                    return 0;
                }

                var measurementIds =
                    eligibleMeasurements.Select(x => x.Id.Value);

                var newMeasurementIds =
                    (await _measurementsRepository
                        .FilterOutHandledMeasurementsAsync(
                            renphoUserId,
                            measurementIds))
                    .ToHashSet();

                var newMeasurements = eligibleMeasurements
                    .Where(x => newMeasurementIds.Contains(x.Id.Value))
                    .OrderBy(x => x.TimeStamp)
                    .ToList();

                if (!newMeasurements.Any())
                {
                    AnsiConsole.MarkupLine(
                        "[green]Aucune nouvelle pesée. Tout est déjà synchronisé.[/]");
                    return 0;
                }

                DrawMeasurementsTable(
                    "Nouvelles pesées à synchroniser",
                    newMeasurements);

                foreach (var measurement in newMeasurements)
                {
                    var fitContent =
                        FitGenerator.GenerateMeasurementFitFile(
                            measurement);

                    var fileName =
                        $"Renpho_{measurement.Id}.fit";

                    var result =
                        await _garminApi.UploadActivity(
                            fileName,
                            fitContent);

                    if (result?.DetailedImportResult?.Failures?.Any() == true)
                    {
                        AnsiConsole.MarkupLineInterpolated(
                            $"[maroon]Échec de l'import Garmin : {fileName}. La pesée sera retentée au prochain passage.[/]");

                        foreach (var failure in
                            result.DetailedImportResult.Failures)
                        {
                            foreach (var message in failure.Messages)
                            {
                                AnsiConsole.MarkupLineInterpolated(
                                    $"\t[maroon]Code : {message.Code}, contenu : {message.Content}[/]");
                            }
                        }

                        await Task.Delay(_random.Next(1000, 1500));
                        continue;
                    }

                    await _measurementsRepository
                        .MarkMeasurementAsHandledAsync(
                            renphoUserId,
                            measurement.Id.Value,
                            measurement.TimeStamp.Value);

                    AnsiConsole.MarkupLineInterpolated(
                        $"[green]Pesée synchronisée avec Garmin : {fileName}[/]");

                    await Task.Delay(_random.Next(1000, 1500));
                }

                return 0;
            }
            catch (GarminException ex)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[maroon]Erreur Garmin : code [yellow]{ex.StatusCode}[/], message : [yellow]{ex.Message}[/][/]");
                return 1;
            }
            catch (RenphoException ex)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[maroon]Erreur RENPHO : code [yellow]{ex.StatusCode}[/], message : [yellow]{ex.Message}[/][/]");
                return 1;
            }
        }

        private static void DrawMeasurementsTable(
            string tableHeader,
            IEnumerable<BodyScaleResponse> measurements)
        {
            var table = new Table();
            table.Title = new TableTitle(tableHeader);

            table.AddColumn("Date");
            table.AddColumn("Poids");
            table.AddColumn("IMC");
            table.AddColumn("ID");

            foreach (var measurement in measurements)
            {
                var measurementTime =
                    DateTimeOffset.FromUnixTimeSeconds(
                        measurement.TimeStamp.Value);

                table.AddRow(
                    measurementTime.ToString("yyyy-MM-dd HH:mm"),
                    $"{measurement.Weight:F1}",
                    $"{measurement.BMI:F1}",
                    $"{measurement.Id}");
            }

            AnsiConsole.Write(table);
        }

        private async Task<IEnumerable<BodyScaleResponse>>
            GetMeasurementsForUserAsync(long userId)
        {
            System.Console.WriteLine(
                "[RENPHO] Récupération des pesées.");

            var deviceInfo =
                await _renphoApi.GetDeviceInfoAsync();

            if (deviceInfo?.Scale == null ||
                !deviceInfo.Scale.Any())
            {
                return [];
            }

            var outWeightIns =
                new List<BodyScaleResponse>();

            foreach (var scaleInfo in deviceInfo.Scale)
            {
                var associatedWithUser =
                    scaleInfo.UserIds?.Contains(userId) == true;

                if (!associatedWithUser)
                    continue;

                var measurements =
                    await _renphoApi.GetBodyScaleMeasurements(
                        userId,
                        scaleInfo.Count,
                        scaleInfo.TableName);

                if (measurements is null)
                    continue;

                var bodyScaleMeasurements = measurements
                    .Where(x => x.UserId == userId)
                    .Where(x =>
                        x.DeviceType
                            .GetDeviceTypeForTag()
                            .IsBodyWeightScale())
                    .ToList();

                outWeightIns.AddRange(bodyScaleMeasurements);
            }

            return outWeightIns
                .OrderByDescending(x => x.TimeStamp)
                .ToList();
        }

        private async Task<long> GetRenphoApiUserIdAsync(
            string targetUserName)
        {
            if (string.IsNullOrWhiteSpace(targetUserName))
                return await _renphoApi.GetLoggedInUserIdAsync();

            var loggedInUserInfo =
                await _renphoApi.GetLoggedInUserInfoAsync();

            if (loggedInUserInfo.AccountName == targetUserName)
                return loggedInUserInfo.Id;

            var guestInfos =
                await _renphoApi.QueryFamilyMembers();

            if (!guestInfos.Any())
            {
                throw new InvalidOperationException(
                    "Logged in user doesn't match the requested name, and no family members were found.");
            }

            var matchingInfo =
                guestInfos.FirstOrDefault(
                    x => x.AccountName == targetUserName);

            if (matchingInfo is null)
            {
                throw new InvalidOperationException(
                    "No Renpho user found matching the requested name.");
            }

            return matchingInfo.Id;
        }
    }
}
