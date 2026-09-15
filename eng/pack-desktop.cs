#!/usr/bin/env dotnet

#:property PublishAot=false
#:property AssemblyName=pack-desktop
#:package System.CommandLine

using System.CommandLine;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

const string publicRepo = "jjonescz/DotNetLab";
const string draftReleaseNotesMode = "draft";

var printReleaseNotesOnly = false;
var releaseNotesMode = (string?)null;
var useDraftRelease = false;
var skipBuild = false;
var ensurePublicCommitHashOnly = false;
var submissionId = (string?)null;
var shouldRun = false;

var printReleaseNotesOption = new Option<string?>("--print-release-notes")
{
    Arity = ArgumentArity.ZeroOrOne,
    Description = "Print the Microsoft Store release notes and exit. Specify 'draft' to use the latest GitHub draft release.",
};
printReleaseNotesOption.Validators.Add(result =>
{
    var value = result.GetValueOrDefault<string?>();
    if (!string.IsNullOrWhiteSpace(value) && !string.Equals(value, draftReleaseNotesMode, StringComparison.OrdinalIgnoreCase))
    {
        result.AddError($"Unsupported --print-release-notes value '{value}'. Supported values: {draftReleaseNotesMode}.");
    }
});
var skipBuildOption = new Option<bool>("--skip-build")
{
    Description = "Skip building the desktop package and use existing package artifacts.",
};
var ensurePublicCommitHashOnlyOption = new Option<bool>("--ensure-public-commit-hash-only")
{
    Description = "Only ensure that a public commit hash is shown inside the app without updating the manifest or building the package.",
};
var submissionIdOption = new Option<string?>("--submission-id")
{
    Description = "Update an existing Microsoft Store draft submission instead of creating a new one.",
};
var rootCommand = new RootCommand("Packs the DotNetLab desktop app package.")
{
    printReleaseNotesOption,
    skipBuildOption,
    ensurePublicCommitHashOnlyOption,
    submissionIdOption,
};
rootCommand.SetAction(parseResult =>
{
    printReleaseNotesOnly = parseResult.GetResult(printReleaseNotesOption) is not null;
    releaseNotesMode = parseResult.GetValue(printReleaseNotesOption);
    useDraftRelease = string.Equals(releaseNotesMode, draftReleaseNotesMode, StringComparison.OrdinalIgnoreCase);
    skipBuild = parseResult.GetValue(skipBuildOption);
    ensurePublicCommitHashOnly = parseResult.GetValue(ensurePublicCommitHashOnlyOption);
    submissionId = parseResult.GetValue(submissionIdOption);
    shouldRun = true;
});

var parseResult = rootCommand.Parse(args);
var parseExitCode = parseResult.Invoke();
if (!shouldRun)
{
    return parseExitCode;
}

Debug.Assert(parseExitCode == 0);

// Find out the version and release notes.
var release = getGitHubRelease(useDraftRelease);
var releaseNotes = getStoreReleaseNotes(
    release.TagName,
    release.Summary,
    release.Body);
if (printReleaseNotesOnly)
{
    Console.WriteLine(releaseNotes);
    return 0;
}

var version = release.TagName.StartsWith('v') ? release.TagName[1..] : release.TagName;
var packageVersion = $"{version}.0";

if (skipBuild)
{
    Console.WriteLine("Manifest update, git reset, and build skipped. Existing package artifacts will be used if upload is enabled.");
}
else if (ensurePublicCommitHashOnly)
{
    ensurePublicCommitHash();
}
else
{
    // Update the manifest.
    var manifestPath = Path.Join(Environment.CurrentDirectory, "eng", "Wap", "Package.appxmanifest");
    var manifestContent = File.ReadAllText(manifestPath);
    var identityIndex = manifestContent.IndexOf("<Identity", StringComparison.Ordinal);
    const string versionPrefix = "Version=\"";
    var versionStartIndex = manifestContent.IndexOf(versionPrefix, identityIndex, StringComparison.Ordinal) + versionPrefix.Length;
    var versionEndIndex = manifestContent.IndexOf('"', versionStartIndex);
    var updatedManifestContent = string.Concat(manifestContent.AsSpan(..versionStartIndex), packageVersion, manifestContent.AsSpan(versionEndIndex..));
    File.WriteAllText(manifestPath, updatedManifestContent);

    // Ensure store association file exists.
    var storeAssociationPath = Path.Join(Environment.CurrentDirectory, "eng", "Wap", "Package.StoreAssociation.xml");
    if (!File.Exists(storeAssociationPath))
    {
        const string storeAssociationEnvVarName = "STORE_ASSOCIATION_FILE_CONTENT";

        var storeAssociationContent = Environment.GetEnvironmentVariable(storeAssociationEnvVarName);

        if (string.IsNullOrWhiteSpace(storeAssociationContent))
        {
            Console.WriteLine($"Warning: store association file not found. Please provide its content via the {storeAssociationEnvVarName} environment variable.");
        }
        else
        {
            File.WriteAllText(storeAssociationPath, storeAssociationContent);
        }
    }

    ensurePublicCommitHash();

    // Build the app package.
    exec("msbuild", "./eng/Wap/Wap.wapproj /v:m /m /bl /restore /p:Configuration=Release /p:Platform=x64 /p:UseRuntimeAsync=true " +
        "/p:RequireDesktopBridge=true /p:UapAppxPackageBuildMode=StoreAndSideload /p:AppxBuildConfigurationSelection=x64");
}

var storeSubmissionConfig = StoreSubmissionConfig.FromEnvironment();
if (storeSubmissionConfig.UploadEnabled)
{
    var packagePath = findStorePackagePath(packageVersion);
    await uploadStoreSubmissionAsync(storeSubmissionConfig, packagePath, releaseNotes, submissionId);
}
else
{
    Console.WriteLine("Store submission upload skipped. Set STORE_UPLOAD_ENABLED=true to create a draft Store submission.");
}

return 0;

void ensurePublicCommitHash()
{
    // Ensure a public commit hash is shown inside the app.
    exec("git", $"fetch origin {release.CommitHash}");
    exec("git", $"reset --soft {release.CommitHash}");
}

GitHubRelease getGitHubRelease(bool draft)
{
    return draft ? getLatestDraftRelease() : getLatestPublishedRelease();
}

GitHubRelease getLatestPublishedRelease()
{
    var release = JsonNode.Parse(exec("gh", $"release view --repo {publicRepo} --json tagName,body"))!.AsObject();
    var tagName = getRequiredString(release, "tagName");
    var commitHash = exec("gh", $"api repos/{publicRepo}/git/ref/tags/{tagName} --jq .object.sha").Trim();

    return new(
        tagName,
        commitHash,
        getReleaseSummary(commitHash),
        release["body"]?.GetValue<string>() ?? string.Empty);
}

GitHubRelease getLatestDraftRelease()
{
    var pages = JsonNode.Parse(exec("gh", $"api repos/{publicRepo}/releases?per_page=100 --paginate --slurp"))?.AsArray()
        ?? throw new InvalidOperationException("Expected a JSON array response when listing GitHub releases.");

    foreach (var page in pages.OfType<JsonArray>())
    {
        foreach (var release in page.OfType<JsonObject>())
        {
            if (release["draft"]?.GetValue<bool>() != true)
            {
                continue;
            }

            var tagName = getRequiredString(release, "tag_name");
            var targetCommitish = getRequiredString(release, "target_commitish");
            var commitHash = exec("gh", $"api repos/{publicRepo}/commits/{Uri.EscapeDataString(targetCommitish)} --jq .sha").Trim();

            return new(
                tagName,
                commitHash,
                getReleaseSummary(commitHash),
                release["body"]?.GetValue<string>() ?? string.Empty);
        }
    }

    throw new InvalidOperationException($"No draft releases were found in {publicRepo}.");
}

string getReleaseSummary(string commitHash)
{
    return exec("gh", $"api repos/{publicRepo}/commits/{commitHash}/pulls --jq .[0].title").Trim();
}

async Task uploadStoreSubmissionAsync(StoreSubmissionConfig config, string packagePath, string releaseNotes, string? submissionId)
{
    var packageFileName = Path.GetFileName(packagePath);
    var packageZipPath = createStoreSubmissionZip(packagePath);
    var accessToken = await getStoreAccessTokenAsync(config);

    using var client = new HttpClient { BaseAddress = new("https://manage.devcenter.microsoft.com") };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DotNetLab-pack-desktop");

    var packageFlightId = config.PackageFlightId.Trim();
    if (config.PublishToFlightAndProduction)
    {
        if (string.IsNullOrWhiteSpace(packageFlightId))
        {
            throw new InvalidOperationException("STORE_PACKAGE_FLIGHT_ID is required when STORE_PUBLISH_TO_FLIGHT_AND_PRODUCTION=true.");
        }

        await uploadSingleStoreSubmissionAsync(
            client,
            config,
            packageZipPath,
            packageFileName,
            releaseNotes,
            submissionId,
            packageFlightId: string.Empty);

        await uploadSingleStoreSubmissionAsync(
            client,
            config,
            packageZipPath,
            packageFileName,
            releaseNotes,
            null,
            packageFlightId);
    }
    else
    {
        await uploadSingleStoreSubmissionAsync(
            client,
            config,
            packageZipPath,
            packageFileName,
            releaseNotes,
            submissionId,
            packageFlightId);
    }
}

static async Task uploadSingleStoreSubmissionAsync(
    HttpClient client,
    StoreSubmissionConfig config,
    string packageZipPath,
    string packageFileName,
    string releaseNotes,
    string? submissionId,
    string packageFlightId)
{
    var submissionsPath = getStoreSubmissionsPath(config, packageFlightId);
    var submissionDescription = getStoreSubmissionDescription(packageFlightId);

    JsonObject submission;
    if (string.IsNullOrWhiteSpace(submissionId))
    {
        Console.WriteLine($"Creating {submissionDescription}");
        submission = await sendStoreRequestAsync(
            client,
            HttpMethod.Post,
            submissionsPath);

        submissionId = getRequiredString(submission, "id");
    }
    else
    {
        submissionId = submissionId.Trim();
        Console.WriteLine($"Loading {submissionDescription} {submissionId}");
        submission = await sendStoreRequestAsync(
            client,
            HttpMethod.Get,
            getStoreSubmissionPath(submissionsPath, submissionId));
    }

    if (isPackageFlight(packageFlightId))
    {
        prepareStoreFlightSubmission(submission, packageFileName);
    }
    else
    {
        prepareStoreSubmission(submission, packageFileName, releaseNotes);
    }

    Console.WriteLine($"Updating {submissionDescription} {submissionId}");
    using var updateContent = new StringContent(submission.ToJsonString(), Encoding.UTF8, "application/json");
    var updatedSubmission = await sendStoreRequestAsync(
        client,
        HttpMethod.Put,
        getStoreSubmissionPath(submissionsPath, submissionId),
        updateContent);

    Console.WriteLine($"Uploading {Path.GetFileName(packageZipPath)} to {submissionDescription} {submissionId}");
    var fileUploadUrl = getRequiredString(updatedSubmission, "fileUploadUrl");
    await uploadStoreSubmissionZipAsync(fileUploadUrl, packageZipPath);

    if (bool.TryParse(Environment.GetEnvironmentVariable("STORE_COMMIT_SUBMISSION"), out var commitSubmission) && commitSubmission)
    {
        await commitStoreSubmissionAsync(client, submissionsPath, submissionDescription, submissionId);
    }
}

static async Task commitStoreSubmissionAsync(HttpClient client, string submissionsPath, string submissionDescription, string submissionId)
{
    Console.WriteLine($"Committing {submissionDescription} {submissionId}");
    var commitResponse = await sendStoreRequestAsync(
        client,
        HttpMethod.Post,
        $"{getStoreSubmissionPath(submissionsPath, submissionId)}/commit");

    var status = getRequiredString(commitResponse, "status");
    Console.WriteLine($"Microsoft Store submission {submissionId} commit status: {status}");
}

static string getStoreSubmissionsPath(StoreSubmissionConfig config, string packageFlightId)
{
    var applicationPath = $"/v1.0/my/applications/{Uri.EscapeDataString(config.ApplicationId)}";
    return isPackageFlight(packageFlightId)
        ? $"{applicationPath}/flights/{Uri.EscapeDataString(packageFlightId)}/submissions"
        : $"{applicationPath}/submissions";
}

static string getStoreSubmissionPath(string submissionsPath, string submissionId)
{
    return $"{submissionsPath}/{Uri.EscapeDataString(submissionId)}";
}

static string getStoreSubmissionDescription(string packageFlightId)
{
    return isPackageFlight(packageFlightId)
        ? $"Microsoft Store package flight {packageFlightId} draft submission"
        : "Microsoft Store draft submission";
}

static bool isPackageFlight(string packageFlightId)
{
    return !string.IsNullOrWhiteSpace(packageFlightId);
}

static async Task<string> getStoreAccessTokenAsync(StoreSubmissionConfig config)
{
    Console.WriteLine("Requesting Microsoft Store API access token");

    using var client = new HttpClient();
    using var content = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "client_credentials",
        ["client_id"] = config.ClientId,
        ["client_secret"] = config.ClientSecret,
        ["resource"] = "https://manage.devcenter.microsoft.com",
    });

    using var response = await client.PostAsync(
        $"https://login.microsoftonline.com/{Uri.EscapeDataString(config.TenantId)}/oauth2/token",
        content);

    var tokenResponse = await readJsonResponseAsync(response);
    return getRequiredString(tokenResponse, "access_token");
}

static async Task<JsonObject> sendStoreRequestAsync(
    HttpClient client,
    HttpMethod method,
    string path,
    HttpContent? content = null)
{
    using var request = new HttpRequestMessage(method, path) { Content = content };
    using var response = await client.SendAsync(request);
    return await readJsonResponseAsync(response);
}

static async Task<JsonObject> readJsonResponseAsync(HttpResponseMessage response)
{
    var content = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        var correlationId = response.Headers.TryGetValues("MS-CorrelationId", out var values)
            ? string.Join(", ", values)
            : "n/a";

        throw new InvalidOperationException(
            $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}. MS-CorrelationId: {correlationId}. Response: {content}");
    }

    return JsonNode.Parse(content)?.AsObject()
        ?? throw new InvalidOperationException("Expected a JSON object response.");
}

static async Task uploadStoreSubmissionZipAsync(string fileUploadUrl, string packageZipPath)
{
    using var client = new HttpClient();
    await using var fileStream = File.OpenRead(packageZipPath);
    using var request = new HttpRequestMessage(HttpMethod.Put, fileUploadUrl.Replace("+", "%2B", StringComparison.Ordinal))
    {
        Content = new StreamContent(fileStream),
    };
    request.Headers.Add("x-ms-blob-type", "BlockBlob");

    using var response = await client.SendAsync(request);
    if (!response.IsSuccessStatusCode)
    {
        var content = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"Package upload failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Response: {content}");
    }
}

static void prepareStoreSubmission(JsonObject submission, string packageFileName, string releaseNotes)
{
    prepareStorePricing(submission);

    var listings = submission["listings"]?.AsObject()
        ?? throw new InvalidOperationException("Store submission response did not contain listings.");

    foreach (var listing in listings)
    {
        var baseListing = listing.Value?["baseListing"]?.AsObject()
            ?? throw new InvalidOperationException($"Store listing '{listing.Key}' did not contain a baseListing object.");

        baseListing["releaseNotes"] = releaseNotes;
    }

    prepareStorePackages(submission, "applicationPackages", packageFileName);
}

static void prepareStoreFlightSubmission(JsonObject submission, string packageFileName)
{
    prepareStorePackages(submission, "flightPackages", packageFileName);
}

static void prepareStorePackages(JsonObject submission, string propertyName, string packageFileName)
{
    var packages = new JsonArray();
    if (submission[propertyName] is JsonArray existingPackages)
    {
        foreach (var existingPackage in existingPackages.OfType<JsonObject>())
        {
            existingPackage["fileStatus"] = "PendingDelete";
            existingPackage["minimumDirectXVersion"] ??= "None";
            existingPackage["minimumSystemRam"] ??= "None";
            packages.Add(existingPackage.DeepClone());
        }
    }

    packages.Add(new JsonObject
    {
        ["fileName"] = packageFileName,
        ["fileStatus"] = "PendingUpload",
        ["minimumDirectXVersion"] = "None",
        ["minimumSystemRam"] = "None",
    });
    submission[propertyName] = packages;
}

static void prepareStorePricing(JsonObject submission)
{
    var pricing = submission["pricing"]?.AsObject()
        ?? throw new InvalidOperationException("Store submission response did not contain pricing.");

    if (string.Equals(pricing["priceId"]?.GetValue<string>(), "Base", StringComparison.OrdinalIgnoreCase))
    {
        const string paidTier = "Tier1122";
        Console.WriteLine($"Fixing up priceId from 'Base' to '{paidTier}'.");
        pricing["priceId"] = paidTier;
    }
}

static string createStoreSubmissionZip(string packagePath)
{
    var packageDirectoryPath = Path.GetDirectoryName(packagePath)!;
    var zipPath = Path.Join(packageDirectoryPath, $"{Path.GetFileNameWithoutExtension(packagePath)}.store-submission.zip");
    if (File.Exists(zipPath))
    {
        File.Delete(zipPath);
    }

    using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
    archive.CreateEntryFromFile(packagePath, Path.GetFileName(packagePath), CompressionLevel.NoCompression);
    return zipPath;
}

static string findStorePackagePath(string packageVersion)
{
    var packageDirectoryPath = Path.Join(Environment.CurrentDirectory, "artifacts", "AppPackages");
    if (!Directory.Exists(packageDirectoryPath))
    {
        throw new DirectoryNotFoundException($"Store package directory not found: {packageDirectoryPath}");
    }

    var packages = Directory.EnumerateFiles(packageDirectoryPath, "*.*", SearchOption.AllDirectories)
        .Where(static filePath =>
            string.Equals(Path.GetExtension(filePath), ".appxupload", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetExtension(filePath), ".msixupload", StringComparison.OrdinalIgnoreCase))
        .Where(filePath => Path.GetFileNameWithoutExtension(filePath).Contains($"_{packageVersion}_", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    return packages.Length switch
    {
        1 => packages[0],
        0 => throw new FileNotFoundException($"No .appxupload or .msixupload package for version {packageVersion} was found under {packageDirectoryPath}."),
        _ => throw new InvalidOperationException($"Expected exactly one Store upload package for version {packageVersion}, but found: " + string.Join(", ", packages)),
    };
}

static string getRequiredString(JsonObject json, string propertyName)
{
    var value = json[propertyName]?.GetValue<string>();
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"JSON property '{propertyName}' was missing or empty.");
    }

    return value;
}

static string getStoreReleaseNotes(string tagName, string releaseSummary, string releaseNotes)
{
    var lines = releaseNotes.ReplaceLineEndings("\n").Split('\n');
    var transformedLines = lines.Select(static line => Regex.Replace(
        line,
        @"\s+\(https://github\.com/[^)]+\)\s*$",
        string.Empty));

    return string.Join(Environment.NewLine + Environment.NewLine,
        $"{tagName}: {toReleaseSummary(releaseSummary)}",
        string.Join(Environment.NewLine, transformedLines).Trim(),
        $"For more details and previous releases, see https://github.com/{publicRepo}/releases");
}

static string toReleaseSummary(string text)
{
    if (string.IsNullOrWhiteSpace(text) || !char.IsUpper(text[0]))
    {
        return text;
    }

    return char.ToLowerInvariant(text[0]) + text[1..];
}

string exec(string command, string args)
{
    Console.Error.WriteLine($"> {command} {args}");

    var output = new StringBuilder();
    var outputLock = new Lock();

    using var process = new Process
    {
        StartInfo =
        {
            FileName = command,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        },
    };

    process.OutputDataReceived += (_, eventArgs) =>
    {
        if (eventArgs.Data is null)
        {
            return;
        }

        lock (outputLock)
        {
            output.AppendLine(eventArgs.Data);
        }

        writeLine('<', eventArgs.Data);
    };

    process.ErrorDataReceived += (_, eventArgs) =>
    {
        if (eventArgs.Data is not null)
        {
            writeLine('!', eventArgs.Data);
        }
    };

    process.Start();
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    process.WaitForExit();

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"Command '{command} {args}' failed with exit code {process.ExitCode}.");
    }

    lock (outputLock)
    {
        return output.ToString();
    }
}

static void writeLine(char prefix, string message)
{
    Console.Error.WriteLine($"{prefix} {message}");
}

file sealed record GitHubRelease(
    string TagName,
    string CommitHash,
    string Summary,
    string Body);

file sealed record StoreSubmissionConfig(
    bool UploadEnabled,
    bool PublishToFlightAndProduction,
    string ApplicationId,
    string TenantId,
    string ClientId,
    string ClientSecret,
    string PackageFlightId)
{
    public static StoreSubmissionConfig FromEnvironment()
    {
        if (!bool.TryParse(Environment.GetEnvironmentVariable("STORE_UPLOAD_ENABLED"), out var uploadEnabled) || !uploadEnabled)
        {
            return new(false, false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        const string defaultPackageFlightId = "ca3ec8ba-40df-41b1-bab6-11ac99c6e6f1";

        var publishToFlightAndProduction = getOptionalBoolEnvVar("STORE_PUBLISH_TO_FLIGHT_AND_PRODUCTION", defaultValue: false);
        var packageFlightId = Environment.GetEnvironmentVariable("STORE_PACKAGE_FLIGHT_ID")?.Trim() ?? string.Empty;
        if (publishToFlightAndProduction && string.IsNullOrWhiteSpace(packageFlightId))
        {
            packageFlightId = defaultPackageFlightId;
        }

        return new(
            true,
            publishToFlightAndProduction,
            getRequiredEnvVar("STORE_APPLICATION_ID"),
            getRequiredEnvVar("STORE_TENANT_ID"),
            getRequiredEnvVar("STORE_CLIENT_ID"),
            getRequiredEnvVar("STORE_CLIENT_SECRET"),
            packageFlightId);

        static bool getOptionalBoolEnvVar(string name, bool defaultValue)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            return bool.TryParse(value, out var result)
                ? result
                : throw new InvalidOperationException($"Environment variable '{name}' must be 'true' or 'false'.");
        }

        static string getRequiredEnvVar(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Environment variable '{name}' is required when STORE_UPLOAD_ENABLED=true.");
            }

            return value;
        }
    }
}
