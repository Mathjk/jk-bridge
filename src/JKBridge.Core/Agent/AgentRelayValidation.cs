using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace JKBridge.Core.Agent;

internal static partial class AgentRelayValidation
{
    [GeneratedRegex("^[a-z][a-z0-9-]{0,47}$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex DnsLabelPattern();

    [GeneratedRegex("^[a-f0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceIdPattern();

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    internal static AgentRelayPlanRequest ValidatePlanRequest(AgentRelayPlanRequest? request)
    {
        if (request?.Spec is null || request.Service is null)
        {
            throw InvalidRequest("Relay plan request is incomplete.");
        }

        var spec = ValidateSpec(request.Spec);
        var service = ValidatePresentation(request.Service);
        return request with { Spec = spec, Service = service };
    }

    internal static AgentRelaySpec ValidateSpec(AgentRelaySpec? spec)
    {
        if (spec is null || !SlugPattern().IsMatch(spec.Slug ?? string.Empty) ||
            spec.RelayPort is < 1 or > 65535 ||
            spec.TargetPort is < 1 or > 65535 ||
            spec.Scheme is not ("http" or "https") ||
            !IsSafeOpenPath(spec.OpenPath))
        {
            throw InvalidRequest("Relay specification is invalid.");
        }

        var targetHost = NormalizeTargetHost(spec.TargetHost);
        if (spec.RelayPort == spec.TargetPort &&
            targetHost is "127.0.0.1" or "localhost")
        {
            throw InvalidRequest("A relay cannot forward back to its own loopback listener.");
        }

        return spec with { TargetHost = targetHost };
    }

    internal static AgentRelayServicePresentation ValidatePresentation(
        AgentRelayServicePresentation? service)
    {
        if (service is null ||
            !IsSafeRequiredText(service.Name, 80) ||
            !IsSafeOptionalText(service.Description, 160) ||
            !IsSafeRequiredText(service.Icon, 32) ||
            service.SuggestedLocalPort is < 1 or > 65535 ||
            !IsSafeHealthPath(service.HealthPath) ||
            service.HealthKind is not (
                AgentHealthKinds.HttpStatus or AgentHealthKinds.JsonOk or AgentHealthKinds.TcpConnect))
        {
            throw InvalidRequest("Relay service presentation is invalid.");
        }

        return service with
        {
            Name = service.Name.Trim(),
            Description = service.Description.Trim(),
            Icon = service.Icon.Trim(),
        };
    }

    internal static AgentRelayPlanResponse ValidatePlanResponse(
        AgentRelayPlanResponse? response,
        string expectedOperation,
        string expectedSlug,
        AgentRelaySpec? expectedSpec,
        AgentRelayPlanToken? expectedToken = null)
    {
        if (response is null || response.ProtocolVersion != AgentProtocol.Version ||
            response.PlanToken is null || response.RelayPlan is null || response.ServiceChange is null ||
            !IsRevision(response.NextServicesRevision))
        {
            throw InvalidResponse("Relay plan response is invalid.");
        }

        if (expectedToken is not null && !response.PlanToken.Equals(expectedToken))
        {
            throw InvalidResponse("Relay apply response does not match the confirmed plan token.");
        }

        ValidatePlan(response.RelayPlan, expectedOperation, expectedSlug, expectedSpec);
        ValidateServiceChange(response.ServiceChange);
        return response;
    }

    internal static AgentRelayApplyResponse ValidateApplyResponse(
        AgentRelayApplyResponse? response,
        string expectedOperation,
        string expectedSlug,
        AgentRelaySpec? expectedSpec,
        AgentRelayPlanToken expectedToken)
    {
        if (response?.Plan is null)
        {
            throw InvalidResponse("Relay apply response is invalid.");
        }

        var plan = ValidatePlanResponse(
            response.Plan,
            expectedOperation,
            expectedSlug,
            expectedSpec,
            expectedToken);
        if (!plan.RelayPlan.CanApply)
        {
            throw InvalidResponse("Agent reported a successful apply for a blocked relay plan.");
        }

        return response with { Plan = plan };
    }

    internal static AgentRelayProbeResponse ValidateProbeResponse(
        AgentRelayProbeResponse? response,
        AgentRelaySpec expectedSpec)
    {
        if (response is null || response.ProtocolVersion != AgentProtocol.Version ||
            response.Spec is null || !IsSafeOptionalText(response.TargetError, 240))
        {
            throw InvalidResponse("Relay probe response is invalid.");
        }

        AgentRelaySpec normalizedSpec;
        try
        {
            normalizedSpec = ValidateSpec(response.Spec);
        }
        catch (AgentProtocolException exception)
        {
            throw InvalidResponse("Relay probe response contains an unsafe target.", exception);
        }

        if (!normalizedSpec.Equals(response.Spec) || !response.Spec.Equals(expectedSpec))
        {
            throw InvalidResponse("Relay probe target differs from the requested exact target.");
        }

        var expectedReady = response.DependencyAvailable &&
                            response.RelayPortInUse is false &&
                            response.TargetReachable is true;
        if (response.ReadyToInstall != expectedReady ||
            response.TargetReachable is true && response.TargetError.Length != 0)
        {
            throw InvalidResponse("Relay probe readiness is internally inconsistent.");
        }

        return response;
    }

    internal static string ValidateSlug(string? slug)
    {
        if (slug is null || !SlugPattern().IsMatch(slug))
        {
            throw InvalidRequest("Relay slug is invalid.");
        }

        return slug;
    }

    internal static void EnsureConfirmed(bool confirmed)
    {
        if (!confirmed)
        {
            throw InvalidRequest("Explicit relay confirmation is required.");
        }
    }

    private static void ValidatePlan(
        AgentRelayPlan plan,
        string expectedOperation,
        string expectedSlug,
        AgentRelaySpec? expectedSpec)
    {
        var validAction = plan.Operation switch
        {
            "install" => plan.Action is "create" or "update" or "no_change" or "blocked",
            "delete" => plan.Action is "delete" or "absent" or "blocked",
            _ => false,
        };
        var shouldApply = plan.Action is not ("blocked" or "absent");
        if (!validAction ||
            !string.Equals(plan.Operation, expectedOperation, StringComparison.Ordinal) ||
            !string.Equals(plan.Slug, expectedSlug, StringComparison.Ordinal) ||
            !string.Equals(plan.UnitName, $"jkbridge-relay-{expectedSlug}.service", StringComparison.Ordinal) ||
            !plan.RequiresConfirmation ||
            plan.CanApply != shouldApply ||
            !IsSafeOptionalText(plan.Reason, 512) ||
            plan.ObservedSha256 is not null && !Sha256Pattern().IsMatch(plan.ObservedSha256))
        {
            throw InvalidResponse("Relay plan fields are invalid or inconsistent.");
        }

        AgentRelaySpec? normalizedSpec = null;
        if (plan.Spec is not null)
        {
            try
            {
                normalizedSpec = ValidateSpec(plan.Spec);
            }
            catch (AgentProtocolException exception)
            {
                throw InvalidResponse("Relay plan contains an unsafe target specification.", exception);
            }

            if (!string.Equals(normalizedSpec.Slug, expectedSlug, StringComparison.Ordinal))
            {
                throw InvalidResponse("Relay plan target does not match its slug.");
            }

            if (!normalizedSpec.Equals(plan.Spec))
            {
                throw InvalidResponse("Relay plan target is not in canonical form.");
            }
        }

        if (expectedSpec is not null &&
            (normalizedSpec is null || !normalizedSpec.Equals(expectedSpec)))
        {
            throw InvalidResponse("Relay plan target differs from the requested exact target.");
        }
    }

    private static void ValidateServiceChange(AgentRelayServiceChange change)
    {
        if (!IsCanonicalPorts(change.AddedRemotePorts) ||
            !IsCanonicalPorts(change.RemovedRemotePorts) ||
            change.AddedRemotePorts.Intersect(change.RemovedRemotePorts).Any() ||
            change.AffectedDeviceIds is null ||
            change.AffectedDeviceIds.Count > AgentProtocol.MaximumDevices ||
            change.AffectedDeviceIds.Any(id => id is null || !DeviceIdPattern().IsMatch(id)) ||
            change.AffectedDeviceIds.Distinct(StringComparer.Ordinal).Count() !=
            change.AffectedDeviceIds.Count ||
            !change.AffectedDeviceIds.SequenceEqual(
                change.AffectedDeviceIds.Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw InvalidResponse("Relay service-change receipt is invalid.");
        }
    }

    private static bool IsCanonicalPorts(IReadOnlyList<int>? ports) =>
        ports is not null &&
        ports.Count <= AgentProtocol.MaximumPermitPorts &&
        ports.All(port => port is >= 1 and <= 65535) &&
        ports.SequenceEqual(ports.Distinct().Order());

    private static string NormalizeTargetHost(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 253 || value != value.Trim() ||
            value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            value.IndexOfAny(['*', '/', '\\', ':', '@', '?', '#']) >= 0)
        {
            throw InvalidRequest("Relay target must be one exact IPv4 address or DNS name.");
        }

        var candidate = value.TrimEnd('.');
        if (candidate.Length == 0)
        {
            throw InvalidRequest("Relay target host is invalid.");
        }

        var numericLooking = candidate.All(character => char.IsAsciiDigit(character) || character == '.');
        if (numericLooking)
        {
            var parts = candidate.Split('.');
            if (parts.Length != 4 || parts.Any(part =>
                    part.Length == 0 ||
                    part.Length > 1 && part[0] == '0' ||
                    !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            {
                throw InvalidRequest("Relay IPv4 target is not canonical dotted decimal.");
            }

            var bytes = parts.Select(byte.Parse).ToArray();
            if (bytes.All(item => item == 0) ||
                bytes[0] is >= 224 and <= 239 ||
                bytes.All(item => item == 255))
            {
                throw InvalidRequest("Relay target cannot be wildcard, multicast, or broadcast.");
            }

            return string.Join('.', bytes);
        }

        var labels = candidate.Split('.');
        if (labels.Any(label => !DnsLabelPattern().IsMatch(label)))
        {
            throw InvalidRequest("Relay target is not a valid DNS name.");
        }

        return candidate.ToLowerInvariant();
    }

    private static bool IsSafeOpenPath(string? path) =>
        !string.IsNullOrEmpty(path) &&
        path.Length <= 512 &&
        path.StartsWith('/') &&
        !path.StartsWith("//", StringComparison.Ordinal) &&
        !path.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));

    private static bool IsSafeHealthPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Length <= 512 &&
        path.StartsWith('/') &&
        !path.StartsWith("//", StringComparison.Ordinal) &&
        !path.Contains('#') &&
        !path.Any(char.IsControl) &&
        Uri.IsWellFormedUriString(path, UriKind.Relative);

    private static bool IsSafeRequiredText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(character => char.IsControl(character) || character == '\u007f');

    private static bool IsSafeOptionalText(string? value, int maximumLength) =>
        value is not null && value.Length <= maximumLength &&
        !value.Any(character => char.IsControl(character) || character == '\u007f');

    private static bool IsRevision(string? value) =>
        value is not null && value.Length is >= 1 and <= 128 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static AgentProtocolException InvalidRequest(string message) =>
        new(message, "invalid_request");

    private static AgentProtocolException InvalidResponse(string message, Exception? inner = null) =>
        new(message, "invalid_response", innerException: inner);
}
