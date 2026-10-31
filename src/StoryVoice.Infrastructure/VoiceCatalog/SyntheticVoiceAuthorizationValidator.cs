using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.Json;

namespace StoryVoice.Infrastructure.VoiceCatalog;

internal sealed record SyntheticVoiceAuthorizationEvidence(
    string SourceKind,
    Guid OwnerId,
    string VoiceAlias,
    Guid CharacterProfileId,
    string DisplayName,
    string? AttributionText,
    IReadOnlyList<string> Styles,
    IReadOnlyList<string> UseCases,
    string FixedDemoSha256,
    string GenerationManifestSha256,
    string TermsSnapshotSha256,
    string ReferenceAudioSha256,
    string TranscriptCanonicalSha256,
    IReadOnlyList<string> AllowedConsumerFamilies,
    string TerritoryMode,
    IReadOnlyList<string> TerritoryCountryCodes,
    string AccountSubjectId,
    string AuditEventId,
    DateTimeOffset AttestedAtUtc,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset EffectiveAtUtc,
    DateTimeOffset ExpiresAtUtc);

internal static class SyntheticVoiceAuthorizationValidator
{
    public const string CurrentSchema = "storyvoice-synthetic-voice-authorization/v1";
    public const string OwnerRecordedSchema = "storyvoice-voice-authorization/v2";
    public const string RequiredRevocationScope = "all-authorized-uses";
    public const int MaximumBytes = 128 * 1024;
    public const int MaximumGenerationManifestBytes = 128 * 1024;
    public const int MaximumTermsSnapshotBytes = 1024 * 1024;

    private static readonly IReadOnlySet<string> RootProperties = Set(
        "schema", "authorizationId", "ownerId", "voice", "creation", "assetBindings",
        "sourceClaims", "providerRights", "permissions",
        "allowedConsumerFamilies", "territory",
        "externalProviderPolicy", "effectiveAtUtc", "expiresAtUtc",
        "revocation", "attestation");

    private static readonly IReadOnlySet<string> OwnerRecordedRootProperties =
        RootProperties.Concat(["sourceKind"]).ToHashSet(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> OwnerRecordedClaims = Set(
        "performerOwnerId", "serviceProviderOwnerId", "recordedByOwner",
        "ownerIsVoicePerformer", "recordingRightsOwned", "noThirdPartyCharacterOrBrandClaimed");
    private static readonly IReadOnlySet<string> OwnerRecordedCreationProperties =
        CreationPropertyNamesForOwnerRecording();
    private static IReadOnlySet<string> CreationPropertyNamesForOwnerRecording() => Set(
        "providerId", "toolId", "modelId", "modelRevision", "createdAtUtc",
        "provenanceManifestSha256", "licenseIdentifier", "termsUri",
        "termsSnapshotSha256", "termsAcceptedAtUtc");

    private static readonly IReadOnlySet<string> VoiceProperties = Set(
        "alias", "characterProfileId", "displayName",
        "attributionText", "attributionDisplayAllowed", "aiDisclosureRequired",
        "styles", "useCases", "fixedDemoSha256", "fixedDemoMediaType");

    private static readonly IReadOnlySet<string> CreationProperties = Set(
        "providerId", "toolId", "modelId", "modelRevision", "createdAtUtc",
        "generationManifestSha256", "licenseIdentifier", "termsUri",
        "termsSnapshotSha256", "termsAcceptedAtUtc");

    private static readonly IReadOnlySet<string> AssetBindingProperties = Set(
        "referenceAudioSha256", "expectedTranscriptCanonicalSha256");

    private static readonly IReadOnlySet<string> SourceClaimProperties = Set(
        "allGenerationInputsOwnedOrLicensed", "noHumanVoiceInputProvided",
        "noHumanBiometricTemplateProvided", "noIdentifiablePersonImitationRequested",
        "noKnownIdentifiablePersonImitated", "noThirdPartyCharacterOrBrandClaimed");

    private static readonly IReadOnlySet<string> ProviderRightProperties = Set(
        "commercialOutputUseAllowed", "publicOutputDistributionAllowed",
        "apiServiceUseAllowed", "voiceModelDerivationAllowed");

    private static readonly IReadOnlySet<string> PermissionProperties = Set(
        "catalogDisplay", "demoPlayback",
        "crossProjectApi", "subscriptionOffering", "commercialUse", "publicDistribution");

    private static readonly IReadOnlySet<string> TerritoryProperties = Set(
        "mode", "countryCodes");

    private static readonly IReadOnlySet<string> ExternalProviderProperties = Set(
        "mode", "allowedProviderIds");

    private static readonly IReadOnlySet<string> RevocationProperties = Set(
        "scope", "contact", "process", "requestedAtUtc", "effectiveAtUtc");

    private static readonly IReadOnlySet<string> AttestationProperties = Set(
        "state", "method", "accountSubjectId", "auditEventId", "attestedAtUtc", "issuedAtUtc");

    public static SyntheticVoiceAuthorizationEvidence Validate(
        ReadOnlySpan<byte> content,
        string alias,
        DateTimeOffset now)
    {
        using var document = ParseDocument(content);
        var ownerRecorded = document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("schema", out var schema)
            && schema.ValueKind == JsonValueKind.String && schema.GetString() == OwnerRecordedSchema;
        var root = ExactObject(document.RootElement, ownerRecorded ? OwnerRecordedRootProperties : RootProperties);
        RequireString(root, "schema", ownerRecorded ? OwnerRecordedSchema : CurrentSchema);
        if (ownerRecorded) RequireString(root, "sourceKind", "owner-recorded");
        RequireIdentifier(GetString(root, "authorizationId", 8, 128));
        var ownerId = CanonicalGuid(root["ownerId"]);

        var voice = ExactObject(root["voice"], VoiceProperties);
        var voiceAlias = GetString(voice, "alias", 1, 64);
        if (!VoiceCatalogOptionsValidator.IsCanonicalAlias(voiceAlias)
            || !string.Equals(voiceAlias, alias, StringComparison.Ordinal))
        {
            throw InvalidOrigin();
        }

        var characterProfileId = CanonicalGuid(voice["characterProfileId"]);
        var presentation = ValidateVoicePresentation(voice);

        var creation = ExactObject(root["creation"], ownerRecorded ? OwnerRecordedCreationProperties : CreationProperties);
        RequireIdentifier(GetString(creation, "providerId", 1, 128));
        _ = GetString(creation, "toolId", 1, 200);
        _ = GetString(creation, "modelId", 1, 200);
        _ = GetString(creation, "modelRevision", 1, 200);
        var createdAtUtc = CanonicalUtc(creation["createdAtUtc"]);
        var generationManifestSha256 = GetRequiredSha256(
            creation,
            ownerRecorded ? "provenanceManifestSha256" : "generationManifestSha256");
        _ = GetString(creation, "licenseIdentifier", 1, 200);
        RequireHttpsUri(GetString(creation, "termsUri", 8, 2_048));
        var termsSnapshotSha256 = GetRequiredSha256(creation, "termsSnapshotSha256");
        var termsAcceptedAtUtc = CanonicalUtc(creation["termsAcceptedAtUtc"]);
        if (termsAcceptedAtUtc > createdAtUtc)
        {
            throw InvalidOrigin();
        }

        var bindings = ExactObject(root["assetBindings"], AssetBindingProperties);
        var referenceAudioSha256 = GetRequiredSha256(bindings, "referenceAudioSha256");
        var transcriptSha256 = GetRequiredSha256(
            bindings,
            "expectedTranscriptCanonicalSha256");
        var claims = ExactObject(root["sourceClaims"], ownerRecorded ? OwnerRecordedClaims : SourceClaimProperties);
        if (ownerRecorded)
        {
            if (CanonicalGuid(claims["performerOwnerId"]) != ownerId
                || CanonicalGuid(claims["serviceProviderOwnerId"]) != ownerId) throw InvalidOrigin();
            foreach (var name in OwnerRecordedClaims.Where(name => !name.EndsWith("OwnerId", StringComparison.Ordinal)))
                RequireTrue(claims, name);
        }
        else
        {
            foreach (var name in SourceClaimProperties) RequireTrue(claims, name);
        }

        var providerRights = ExactObject(root["providerRights"], ProviderRightProperties);
        foreach (var name in ProviderRightProperties)
        {
            RequireTrue(providerRights, name);
        }

        var permissions = ExactObject(root["permissions"], PermissionProperties);
        foreach (var name in PermissionProperties)
        {
            RequireTrue(permissions, name);
        }

        var allowedConsumerFamilies = IdentifierArray(
            root["allowedConsumerFamilies"],
            1,
            50);
        var territory = ValidateTerritory(root["territory"]);
        ValidateExternalProviderPolicy(root["externalProviderPolicy"]);

        var effectiveAtUtc = CanonicalUtc(root["effectiveAtUtc"]);
        var expiresAtUtc = CanonicalUtc(root["expiresAtUtc"]);
        if (expiresAtUtc <= effectiveAtUtc || now < effectiveAtUtc || now >= expiresAtUtc)
        {
            throw InvalidOrigin();
        }

        ValidateRevocation(root["revocation"]);

        var attestation = ExactObject(root["attestation"], AttestationProperties);
        RequireString(attestation, "state", "active");
        RequireString(attestation, "method", "authenticated-owner-action");
        var accountSubjectId = GetString(attestation, "accountSubjectId", 1, 200);
        RequireIdentifier(accountSubjectId);
        var auditEventId = GetString(attestation, "auditEventId", 1, 200);
        RequireIdentifier(auditEventId);
        var attestedAtUtc = CanonicalUtc(attestation["attestedAtUtc"]);
        var issuedAtUtc = CanonicalUtc(attestation["issuedAtUtc"]);
        if (createdAtUtc > attestedAtUtc
            || attestedAtUtc > issuedAtUtc
            || issuedAtUtc > effectiveAtUtc)
        {
            throw InvalidOrigin();
        }

        return new SyntheticVoiceAuthorizationEvidence(
            ownerRecorded ? "owner-recorded" : "original-synthetic",
            ownerId,
            voiceAlias,
            characterProfileId,
            presentation.DisplayName,
            presentation.AttributionText,
            presentation.Styles,
            presentation.UseCases,
            presentation.FixedDemoSha256,
            generationManifestSha256,
            termsSnapshotSha256,
            referenceAudioSha256,
            transcriptSha256,
            allowedConsumerFamilies,
            territory.Mode,
            territory.CountryCodes,
            accountSubjectId,
            auditEventId,
            attestedAtUtc,
            issuedAtUtc,
            effectiveAtUtc,
            expiresAtUtc);
    }

    private static VoicePresentation ValidateVoicePresentation(
        IReadOnlyDictionary<string, JsonElement> voice)
    {
        var displayName = GetString(voice, "displayName", 1, 120);
        RejectUnsupportedOfficialClaim(displayName);
        var attribution = voice["attributionText"];
        string? attributionText;
        if (attribution.ValueKind == JsonValueKind.Null)
        {
            attributionText = null;
        }
        else if (attribution.ValueKind == JsonValueKind.String
            && IsPrintable(attribution.GetString()!, 1, 500))
        {
            attributionText = attribution.GetString()!;
            RejectUnsupportedOfficialClaim(attributionText);
        }
        else
        {
            throw InvalidOrigin();
        }

        RequireTrue(voice, "attributionDisplayAllowed");
        RequireTrue(voice, "aiDisclosureRequired");
        var styles = StringArray(
            voice["styles"],
            1,
            8,
            IsSafeLabel);
        var useCases = StringArray(
            voice["useCases"],
            1,
            8,
            IsSafeLabel);
        var fixedDemoSha256 = GetRequiredSha256(voice, "fixedDemoSha256");
        RequireString(voice, "fixedDemoMediaType", "audio/wav");
        return new VoicePresentation(
            displayName,
            attributionText,
            styles,
            useCases,
            fixedDemoSha256);
    }

    private static TerritoryAuthorization ValidateTerritory(JsonElement element)
    {
        var territory = ExactObject(element, TerritoryProperties);
        var mode = GetString(territory, "mode", 1, 20);
        var minimum = mode == "worldwide" ? 0 : 1;
        var maximum = mode == "worldwide" ? 0 : 249;
        if (mode is not ("worldwide" or "country-list"))
        {
            throw InvalidOrigin();
        }

        var countryCodes = StringArray(
            territory["countryCodes"],
            minimum,
            maximum,
            VoiceCatalogOptionsValidator.IsCountryCode);
        return new TerritoryAuthorization(mode, countryCodes);
    }

    private static void ValidateExternalProviderPolicy(JsonElement element)
    {
        var policy = ExactObject(element, ExternalProviderProperties);
        RequireString(policy, "mode", "prohibited");
        _ = IdentifierArray(policy["allowedProviderIds"], 0, 0);
    }

    private static void ValidateRevocation(JsonElement element)
    {
        var revocation = ExactObject(element, RevocationProperties);
        RequireString(revocation, "scope", RequiredRevocationScope);
        var contact = GetString(revocation, "contact", 3, 320);
        if (!IsEmailOrHttps(contact))
        {
            throw InvalidOrigin();
        }

        _ = GetString(revocation, "process", 20, 2_000);
        if (revocation["requestedAtUtc"].ValueKind != JsonValueKind.Null
            || revocation["effectiveAtUtc"].ValueKind != JsonValueKind.Null)
        {
            throw InvalidOrigin();
        }
    }

    private static JsonDocument ParseDocument(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty
            || content.Length > MaximumBytes
            || content.Length >= 3
                && content[0] == 0xef
                && content[1] == 0xbb
                && content[2] == 0xbf)
        {
            throw InvalidOrigin();
        }

        string json;
        try
        {
            json = new UTF8Encoding(false, true).GetString(content);
        }
        catch (DecoderFallbackException exception)
        {
            throw InvalidOrigin(exception);
        }

        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12,
            });
        }
        catch (JsonException exception)
        {
            throw InvalidOrigin(exception);
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> ExactObject(
        JsonElement element,
        IReadOnlySet<string> allowedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidOrigin();
        }

        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowedProperties.Contains(property.Name)
                || !properties.TryAdd(property.Name, property.Value))
            {
                throw InvalidOrigin();
            }
        }

        if (properties.Count != allowedProperties.Count
            || allowedProperties.Any(property => !properties.ContainsKey(property)))
        {
            throw InvalidOrigin();
        }

        return properties;
    }

    private static string GetString(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        int minimumLength,
        int maximumLength)
    {
        var element = properties[name];
        if (element.ValueKind != JsonValueKind.String
            || !IsPrintable(element.GetString()!, minimumLength, maximumLength))
        {
            throw InvalidOrigin();
        }

        return element.GetString()!;
    }

    private static IReadOnlyList<string> IdentifierArray(
        JsonElement element,
        int minimumCount,
        int maximumCount) =>
        StringArray(
            element,
            minimumCount,
            maximumCount,
            VoiceCatalogOptionsValidator.IsCanonicalGrantIdentifier);

    private static IReadOnlyList<string> StringArray(
        JsonElement element,
        int minimumCount,
        int maximumCount,
        Func<string, bool> validator)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw InvalidOrigin();
        }

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || !validator(item.GetString()!)
                || values.Contains(item.GetString()!, StringComparer.Ordinal))
            {
                throw InvalidOrigin();
            }

            values.Add(item.GetString()!);
        }

        if (values.Count < minimumCount || values.Count > maximumCount)
        {
            throw InvalidOrigin();
        }

        return values;
    }

    private static void RequireString(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        string expected)
    {
        if (properties[name].ValueKind != JsonValueKind.String
            || !string.Equals(properties[name].GetString(), expected, StringComparison.Ordinal))
        {
            throw InvalidOrigin();
        }
    }

    private static Guid CanonicalGuid(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(element.GetString(), "D", out var parsed)
            || parsed == Guid.Empty
            || !string.Equals(element.GetString(), parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw InvalidOrigin();
        }

        return parsed;
    }

    private static string GetRequiredSha256(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name)
    {
        var value = GetString(properties, name, 64, 64);
        RequireSha256(value);
        return value;
    }

    private static void RequireSha256(string value)
    {
        if (!VoiceCatalogOptionsValidator.IsCanonicalSha256(value))
        {
            throw InvalidOrigin();
        }
    }

    private static void RequireIdentifier(string value)
    {
        if (!VoiceCatalogOptionsValidator.IsCanonicalGrantIdentifier(value))
        {
            throw InvalidOrigin();
        }
    }

    private static void RequireHttpsUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw InvalidOrigin();
        }
    }

    private static DateTimeOffset CanonicalUtc(JsonElement element)
    {
        const string format = "yyyy-MM-dd'T'HH:mm:ss'Z'";
        if (element.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParseExact(
                element.GetString(),
                format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
            || !string.Equals(
                element.GetString(),
                parsed.ToString(format, CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw InvalidOrigin();
        }

        return parsed;
    }

    private static void RequireTrue(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name)
    {
        if (properties[name].ValueKind != JsonValueKind.True)
        {
            throw InvalidOrigin();
        }
    }

    private static void RequireFalse(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name)
    {
        if (properties[name].ValueKind != JsonValueKind.False)
        {
            throw InvalidOrigin();
        }
    }

    private static bool IsPrintable(string value, int minimumLength, int maximumLength) =>
        value.Length >= minimumLength
        && value.Length <= maximumLength
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && value.All(character => !char.IsControl(character)
            && char.GetUnicodeCategory(character) != UnicodeCategory.Format);

    private static bool IsSafeLabel(string value)
    {
        if (!IsPrintable(value, 1, 40))
        {
            return false;
        }

        RejectUnsupportedOfficialClaim(value);
        return true;
    }

    private static void RejectUnsupportedOfficialClaim(string value)
    {
        if (value.Contains("官方", StringComparison.Ordinal)
            || value.Contains("official", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidOrigin();
        }
    }

    private static bool IsEmailOrHttps(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            && string.IsNullOrEmpty(uri.UserInfo))
        {
            return true;
        }

        return MailAddress.TryCreate(value, out var email)
            && string.Equals(email.Address, value, StringComparison.Ordinal);
    }

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);

    private static InvalidDataException InvalidOrigin(Exception? innerException = null) =>
        new("Synthetic voice authorization evidence is invalid.", innerException);

    private sealed record VoicePresentation(
        string DisplayName,
        string? AttributionText,
        IReadOnlyList<string> Styles,
        IReadOnlyList<string> UseCases,
        string FixedDemoSha256);

    private sealed record TerritoryAuthorization(
        string Mode,
        IReadOnlyList<string> CountryCodes);
}
