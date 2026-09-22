using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Application.Reporting;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Host.AllInOne;
using EnterpriseObservatory.Persistence.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// The real composition root, booted and asked who may do what.
/// </summary>
/// <remarks>
/// <para>
/// Program.cs went a week without a test, and the roadmap said what that
/// would cost: deleting <c>UseAuthorization()</c> turns every
/// <c>RequireAuthorization</c> into a no-op, deleting the
/// <c>OnValidatePrincipal</c> handler leaves a removed account twelve hours
/// of access, and no test anywhere would fail. The first bill arrived as an
/// outage — two endpoints sharing a name took the whole interface down with
/// the suite green.
/// </para>
/// <para>
/// These tests boot Program.cs itself, not a copy of its wiring. Only the
/// stores are swapped for in-memory ones and the two background workers are
/// removed; authentication, authorization, the cookie scheme, its events, the
/// policies and every endpoint are the production ones. That is the point of
/// the exercise: a smoke test that rebuilt the pipeline by hand would test the
/// hand-built pipeline.
/// </para>
/// <para>
/// **Every negative here has a positive beside it.** A 401 or 403 proves
/// nothing on its own — a pipeline that refused everyone would pass — so each
/// refusal is paired with the same caller succeeding where they should. The
/// first registration test in this repository failed all its assertions for a
/// setup reason that looked exactly like the bug it was hunting; the pairs are
/// what stop that happening here.
/// </para>
/// <para>
/// What this does not cover, said so nobody assumes it: PostgreSQL. No store
/// here touches a database, and any attempt to build one throws. The 68 live
/// persistence tests are what cover the database, and they run in CI.
/// </para>
/// </remarks>
public sealed class CompositionRootSmokeTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly ObservatoryHost _host = new();

    public void Dispose() => _host.Dispose();

    private HttpClient Client() => _host.CreateClient(new WebApplicationFactoryClientOptions
    {
        // A redirect would hide exactly the status these tests read. The API
        // answers with 401 and 403 rather than a login page, and following a
        // redirect would turn a regression there into a 200 from index.html.
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    private void Account(string username, Role role) =>
        Assert.True(_host.Accounts.TryAdd(new UserAccount
        {
            Username = username,
            // Deliberately cheap. The hash's strength is tested elsewhere; here
            // it only has to verify, and the default work factor would make
            // this class the slowest in the suite for no information.
            Password = PasswordHash.Create(Secret.From(Password), iterations: 1_000),
            Role = role,
            CreatedUtc = DateTimeOffset.UtcNow,
        }));

    private static async Task<HttpClient> SignedIn(HttpClient client, string username)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/signin", new { username, password = Password });

        // If signing in failed, every assertion after this would be about an
        // anonymous caller. Stop here rather than let a 401 masquerade as the
        // behaviour under test.
        Assert.True(
            response.IsSuccessStatusCode,
            $"Sign-in as '{username}' failed with {(int)response.StatusCode}; " +
            "the tests that follow would be about an anonymous caller.");

        return client;
    }

    private static Task<HttpResponseMessage> Acknowledge(HttpClient client) =>
        client.PostAsJsonAsync("/api/alerts/acknowledge", new { fingerprint = "smoke|test|x|y|z" });

    // --- anonymous ---------------------------------------------------------

    [Fact]
    public async Task An_anonymous_caller_is_refused_with_401()
    {
        var response = await Client().GetAsync("/api/overview");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_sign_in_surface_is_reachable_without_a_session()
    {
        // The positive control for the test above. If the pipeline refused
        // everyone, the 401 there would still pass; this proves the refusal is
        // a decision about that endpoint rather than about every request.
        var response = await Client().GetAsync("/api/auth/state");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Every_api_endpoint_outside_sign_in_refuses_an_anonymous_caller()
    {
        // The roadmap's named mutation: remove UseAuthorization() and every
        // RequireAuthorization becomes decorative. Asked of every GET the
        // product maps rather than of one, because the failure it guards
        // against is an endpoint somebody added without the requirement --
        // and that endpoint is, by definition, not the one a hand-picked test
        // would name.
        var client = Client();

        var paths = _host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true)
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .Where(p => p.StartsWith("/api/", StringComparison.Ordinal) &&
                        !p.StartsWith("/api/auth", StringComparison.Ordinal))
            .Select(p => p.Replace("{id}", "x", StringComparison.Ordinal)
                          .Replace("{instanceId}", "x", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(paths);

        foreach (var path in paths)
        {
            var response = await client.GetAsync(path);

            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"GET {path} answered an anonymous caller with {(int)response.StatusCode}.");
        }
    }

    // --- a viewer ----------------------------------------------------------

    [Fact]
    public async Task A_viewer_can_read()
    {
        // The positive control for the viewer's 403. Without it, a viewer who
        // could not sign in at all would pass the refusal test for the wrong
        // reason.
        Account("viewer", Role.Viewer);
        var client = await SignedIn(Client(), "viewer");

        var response = await client.GetAsync("/api/overview");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_can_read_the_vcenter_event_feed()
    {
        // The positive for the event feed's anonymous 401 above, and proof the
        // event store is wired: an unregistered one answers 500 here.
        Account("viewer", Role.Viewer);
        var client = await SignedIn(Client(), "viewer");

        var response = await client.GetAsync("/api/vcenter-events");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_can_read_the_alert_report_as_csv()
    {
        // Reads are for any signed-in user, same as the alert list this
        // report is a slice of -- printing or exporting it is not a more
        // sensitive act than reading the inbox.
        Account("viewer", Role.Viewer);
        var client = await SignedIn(Client(), "viewer");

        var response = await client.GetAsync("/api/reports/alerts.csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_the_alert_report_csv_with_401()
    {
        // The report endpoints are covered by the sweep above too, but this
        // one is named: it is the export an operator prints to PDF from, and
        // it carries the estate's alert history, not only its inventory.
        var response = await Client().GetAsync("/api/reports/alerts.csv");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_can_read_the_capacity_report_as_csv()
    {
        // M5.3, the same read-for-any-signed-in-user rule as the alert report.
        Account("viewer", Role.Viewer);
        var client = await SignedIn(Client(), "viewer");

        var response = await client.GetAsync("/api/reports/capacity.csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_the_capacity_report_csv_with_401()
    {
        var response = await Client().GetAsync("/api/reports/capacity.csv");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_can_read_the_continuity_report_as_csv()
    {
        // M8.10, the same read-for-any-signed-in-user rule as the other three reports.
        Account("viewer", Role.Viewer);
        var client = await SignedIn(Client(), "viewer");

        var response = await client.GetAsync("/api/reports/continuity.csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_the_continuity_report_csv_with_401()
    {
        var response = await Client().GetAsync("/api/reports/continuity.csv");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_who_tries_to_change_something_is_refused_with_403()
    {
        Account("viewer", Role.Viewer);
        var client = await SignedIn(Client(), "viewer");

        var response = await Acknowledge(client);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_operator_gets_past_the_policy_a_viewer_is_stopped_at()
    {
        // The other half of the pair. The body names an alert that does not
        // exist, so whatever the handler says is not the point; what matters
        // is that it was reached, which neither 401 nor 403 would mean.
        Account("operator", Role.Operator);
        var client = await SignedIn(Client(), "operator");

        var response = await Acknowledge(client);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- a session that outlives its account --------------------------------

    [Fact]
    public async Task A_session_ends_when_its_account_is_removed()
    {
        // The one test here no stub scheme could run. Replacing the cookie
        // handler with a test handler -- the usual recipe -- means the real
        // OnValidatePrincipal never executes, so it would pass whether or not
        // the handler exists. This drives the production cookie: sign in, get
        // a real session, remove the account, and ask again.
        Account("leaver", Role.Operator);
        var client = await SignedIn(Client(), "leaver");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/overview")).StatusCode);

        Assert.True(_host.Accounts.Remove("leaver"));

        var afterwards = await client.GetAsync("/api/overview");

        Assert.Equal(HttpStatusCode.Unauthorized, afterwards.StatusCode);
    }

    [Fact]
    public async Task A_demotion_takes_effect_on_the_next_request()
    {
        // The same handler in the other direction. A cookie carries the role
        // it was issued with; without the refresh, an operator demoted to
        // viewer keeps writing until the cookie expires -- twelve hours.
        Account("demoted", Role.Operator);
        var client = await SignedIn(Client(), "demoted");

        Assert.NotEqual(HttpStatusCode.Forbidden, (await Acknowledge(client)).StatusCode);

        _host.Accounts.Mutate("demoted", a => a with { Role = Role.Viewer });

        Assert.Equal(HttpStatusCode.Forbidden, (await Acknowledge(client)).StatusCode);
    }

    // --- compliance ---------------------------------------------------------

    // The host sends enums as names; see Program.cs.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static Task<HttpResponseMessage> AcceptFinding(HttpClient client) =>
        client.PostAsJsonAsync(
            "/api/compliance/accept",
            new { controlId = "esxi-8.logs-remote", entityId = "vc-1:host-1", reason = "smoke" });

    [Fact]
    public async Task A_viewer_can_read_the_compliance_screen_and_its_catalogue_is_loaded()
    {
        // The positive for the anonymous 401 every GET gets above, and proof
        // that the catalogue shipped beside the binary was found: a host that
        // lost it would answer 200 with a problem instead of controls.
        Account("viewer", Role.Viewer);
        var client = await SignedIn(Client(), "viewer");

        var response = await client.GetAsync("/api/compliance");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<Api.ComplianceView>(Json);

        Assert.NotNull(body);
        Assert.Null(body.CatalogueProblem);
        Assert.Equal("803-20260612-01", body.CatalogueRelease);
        Assert.Contains(body.Controls, c => c.ControlId == "esxi-8.logs-remote" && c.Evaluated);
        Assert.Contains(body.Controls, c => !c.Evaluated && c.NotEvaluatedReason is not null);

        Assert.Equal(
            HttpStatusCode.OK, (await client.GetAsync("/api/compliance/findings?state=Failing")).StatusCode);
    }

    [Fact]
    public async Task A_viewer_who_tries_to_accept_a_finding_is_refused_with_403()
    {
        Account("viewer", Role.Viewer);
        var client = await SignedIn(Client(), "viewer");

        Assert.Equal(HttpStatusCode.Forbidden, (await AcceptFinding(client)).StatusCode);
    }

    // --- compliance report (M5.2) -------------------------------------------

    [Fact]
    public async Task A_viewer_can_read_the_compliance_report()
    {
        Account("viewer", Role.Viewer);
        var client = await SignedIn(Client(), "viewer");

        var response = await client.GetAsync("/api/reports/compliance");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<Api.Contracts.ComplianceReportView>(Json);

        Assert.NotNull(body);
        Assert.Equal("All hosts", body.Scope);
    }

    [Fact]
    public async Task A_viewer_can_read_the_compliance_report_as_csv()
    {
        // Reads are for any signed-in user, the same line the compliance
        // screen and the alert report both draw: exporting a finding for an
        // auditor is not a more sensitive act than reading it.
        Account("viewer", Role.Viewer);
        var client = await SignedIn(Client(), "viewer");

        var response = await client.GetAsync("/api/reports/compliance.csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_viewer_can_read_the_compliance_report_history_as_csv()
    {
        // The report's other CSV section -- see ComplianceApi.cs for why it
        // is a query param on the same endpoint rather than a second one.
        Account("viewer", Role.Viewer);
        var client = await SignedIn(Client(), "viewer");

        var response = await client.GetAsync("/api/reports/compliance.csv?section=history");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_the_compliance_report_csv_with_401()
    {
        // Covered by the sweep above too, but named: it is the export an
        // auditor is handed, and it carries the estate's compliance history,
        // not only its current state.
        var response = await Client().GetAsync("/api/reports/compliance.csv");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void The_compliance_service_judges_the_vendor_guide_first_and_the_continuity_catalogue_beside_it()
    {
        // K2: the product's own catalogue carries the M8 continuity checks,
        // every one bound, beside the vendor guide.
        var compliance = _host.Services.GetRequiredService<ComplianceService>();

        Assert.Equal(2, compliance.Catalogues.Count);
        Assert.Same(_host.Services.GetRequiredService<Domain.Compliance.ComplianceCatalogue>(), compliance.Catalogue);
        Assert.Equal(ContinuityCatalogue.Release, compliance.Catalogues[1].Release);
        Assert.Equal(
            ContinuityCatalogue.Production.Select(c => c.Control.ControlId),
            compliance.Catalogues[1].Controls.Select(c => c.ControlId));
        Assert.All(
            compliance.Controls().Where(c => c.CatalogueRelease == ContinuityCatalogue.Release),
            c => Assert.True(c.IsEvaluated));
    }

    [Fact]
    public async Task An_operator_can_accept_a_failing_finding_and_it_is_attributed()
    {
        // Evaluated through the real service the host registered, over a
        // host whose log target is empty -- then accepted over HTTP.
        var compliance = _host.Services.GetRequiredService<ComplianceService>();
        compliance.Evaluate(
        [
            new Domain.Entity
            {
                Id = new Domain.EntityId("vc-1:host-1"),
                Kind = Domain.EntityKind.EsxiHost,
                DisplayName = "esx-01",
                LastSeenUtc = DateTimeOffset.UtcNow,
                Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Syslog.global.logHost"] = "",
                },
            },
        ]);

        Account("operator", Role.Operator);
        var client = await SignedIn(Client(), "operator");

        var response = await AcceptFinding(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var finding = await response.Content.ReadFromJsonAsync<Api.ComplianceFindingView>(Json);

        Assert.Equal(Domain.Compliance.FindingState.Accepted, finding!.State);
        Assert.Equal("operator", finding.AcceptedBy);
        Assert.False(finding.Stale);

        // A second acceptance is a conflict, not an overwrite.
        Assert.Equal(HttpStatusCode.Conflict, (await AcceptFinding(client)).StatusCode);
    }

    [Fact]
    public async Task Removing_an_exception_over_http_records_who_removed_it_and_keeps_it_listed()
    {
        Account("operator", Role.Operator);
        var client = await SignedIn(Client(), "operator");

        var added = await client.PostAsJsonAsync("/api/compliance/exceptions", new
        {
            controlId = "esxi-8.logs-remote",
            entityId = (string?)null,
            reason = "smoke",
            owner = "infra",
            expiresUtc = DateTimeOffset.UtcNow.AddDays(7),
        });

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        var exception = await added.Content.ReadFromJsonAsync<Api.ComplianceExceptionView>(Json);

        var removed = await client.PostAsJsonAsync("/api/compliance/exceptions/remove", new { id = exception!.Id });

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        var summary = await client.GetFromJsonAsync<Api.ComplianceView>("/api/compliance", Json);

        Assert.DoesNotContain(summary!.Exceptions, e => e.Id == exception.Id);

        var all = await client.GetFromJsonAsync<List<Api.ComplianceExceptionView>>(
            "/api/compliance/exceptions?includeRemoved=true", Json);

        var kept = Assert.Single(all!, e => e.Id == exception.Id);

        Assert.Equal("operator", kept.RemovedBy);
        Assert.NotNull(kept.RemovedAtUtc);
    }

    [Fact]
    public async Task An_over_long_exception_reason_is_refused_with_a_reason()
    {
        Account("operator", Role.Operator);
        var client = await SignedIn(Client(), "operator");

        var response = await client.PostAsJsonAsync("/api/compliance/exceptions", new
        {
            controlId = "esxi-8.logs-remote",
            entityId = (string?)null,
            reason = new string('x', ComplianceService.MaximumReasonLength + 1),
            owner = "infra",
            expiresUtc = DateTimeOffset.UtcNow.AddDays(7),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            ComplianceService.MaximumReasonLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    // --- scheduled email reports (M5.4) -------------------------------------

    [Fact]
    public async Task A_viewer_is_refused_the_smtp_settings_with_403()
    {
        Account("viewer", Role.Viewer);
        var client = await SignedIn(Client(), "viewer");

        var response = await client.GetAsync("/api/email/settings");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_administrator_can_read_and_update_smtp_settings_and_the_password_never_comes_back()
    {
        Account("root", Role.Administrator);
        var client = await SignedIn(Client(), "root");

        var initial = await client.GetFromJsonAsync<Api.SmtpSettingsView>("/api/email/settings", Json);
        Assert.NotNull(initial);
        Assert.Equal("not set", initial.PasswordStatus);

        var update = await client.PutAsJsonAsync("/api/email/settings", new
        {
            host = "smtp.example.com",
            port = 587,
            tlsMode = "StartTls",
            fromAddress = "observatory@example.com",
            username = "observatory",
            password = "hunter2-hunter2-hunter2",
            allowUnencrypted = false,
        });

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var body = await update.Content.ReadAsStringAsync();

        Assert.DoesNotContain("hunter2", body, StringComparison.Ordinal);

        var saved = await update.Content.ReadFromJsonAsync<Api.SmtpSettingsView>(Json);
        Assert.Equal("set", saved!.PasswordStatus);
        Assert.True(saved.IsConfigured);

        // An operator neither reads nor sets these -- Administrator only, both
        // verbs, like ConnectionsApi.
        Account("op", Role.Operator);
        var opClient = await SignedIn(Client(), "op");
        Assert.Equal(HttpStatusCode.Forbidden, (await opClient.GetAsync("/api/email/settings")).StatusCode);
    }

    [Fact]
    public async Task An_operator_can_manage_report_subscriptions_but_a_viewer_cannot()
    {
        Account("viewer", Role.Viewer);
        var viewer = await SignedIn(Client(), "viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/reports/subscriptions")).StatusCode);

        Account("op", Role.Operator);
        var client = await SignedIn(Client(), "op");

        string[] recipients = ["team@example.com"];

        var created = await client.PostAsJsonAsync("/api/reports/subscriptions", new
        {
            recipients,
            frequency = "Daily",
            dayOfWeek = "Monday",
            hourLocal = 7,
            timeZoneId = "UTC",
            kind = "Alerts",
            isEnabled = true,
        });

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var subscription = await created.Content.ReadFromJsonAsync<Api.ReportSubscriptionView>(Json);
        Assert.Equal("op", subscription!.CreatedBy);
        Assert.Null(subscription.LastSentUtc);

        var removed = await client.DeleteAsync($"/api/reports/subscriptions/{subscription.Id}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    }

    [Fact]
    public async Task An_operator_cannot_edit_or_delete_another_operators_subscription_but_an_administrator_can()
    {
        // Architecture review 3: any Operator could edit or delete anyone's
        // subscription and redirect it externally, with nothing recorded
        // about who did it. Only the creator or an Administrator may now.
        Account("owner", Role.Operator);
        var owner = await SignedIn(Client(), "owner");

        string[] ownerRecipients = ["team@example.com"];

        var created = await owner.PostAsJsonAsync("/api/reports/subscriptions", new
        {
            recipients = ownerRecipients,
            frequency = "Daily",
            dayOfWeek = "Monday",
            hourLocal = 7,
            timeZoneId = "UTC",
            kind = "Alerts",
            isEnabled = true,
        });

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var subscription = await created.Content.ReadFromJsonAsync<Api.ReportSubscriptionView>(Json);
        Assert.NotNull(subscription);

        Account("other", Role.Operator);
        var other = await SignedIn(Client(), "other");

        string[] attackerRecipients = ["attacker@example.com"];

        var editByOther = await other.PutAsJsonAsync($"/api/reports/subscriptions/{subscription!.Id}", new
        {
            recipients = attackerRecipients,
            frequency = "Daily",
            dayOfWeek = "Monday",
            hourLocal = 7,
            timeZoneId = "UTC",
            kind = "Alerts",
            isEnabled = true,
        });

        Assert.Equal(HttpStatusCode.Forbidden, editByOther.StatusCode);

        var deleteByOther = await other.DeleteAsync($"/api/reports/subscriptions/{subscription.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, deleteByOther.StatusCode);

        // The positive control: the creator may still edit their own, and
        // the edit is stamped with who made it.
        string[] twoRecipients = ["team@example.com", "second@example.com"];

        var editByOwner = await owner.PutAsJsonAsync($"/api/reports/subscriptions/{subscription.Id}", new
        {
            recipients = twoRecipients,
            frequency = "Daily",
            dayOfWeek = "Monday",
            hourLocal = 7,
            timeZoneId = "UTC",
            kind = "Alerts",
            isEnabled = true,
        });

        Assert.Equal(HttpStatusCode.OK, editByOwner.StatusCode);

        var edited = await editByOwner.Content.ReadFromJsonAsync<Api.ReportSubscriptionView>(Json);
        Assert.Equal("owner", edited!.LastModifiedBy);
        Assert.NotNull(edited.LastModifiedUtc);

        // The other positive control: an Administrator may act on anyone's.
        Account("root", Role.Administrator);
        var admin = await SignedIn(Client(), "root");

        var editByAdmin = await admin.PutAsJsonAsync($"/api/reports/subscriptions/{subscription.Id}", new
        {
            recipients = ownerRecipients,
            frequency = "Weekly",
            dayOfWeek = "Friday",
            hourLocal = 9,
            timeZoneId = "UTC",
            kind = "Alerts",
            isEnabled = true,
        });

        Assert.Equal(HttpStatusCode.OK, editByAdmin.StatusCode);

        var removeByAdmin = await admin.DeleteAsync($"/api/reports/subscriptions/{subscription.Id}");
        Assert.Equal(HttpStatusCode.NoContent, removeByAdmin.StatusCode);
    }

    [Fact]
    public async Task A_numeric_string_outside_the_named_range_is_refused_rather_than_silently_accepted()
    {
        // Enum.TryParse alone accepts "7" as a DayOfWeek even though nothing
        // names it; a value the product cannot render or schedule against
        // must be refused at the door instead.
        Account("op", Role.Operator);
        var client = await SignedIn(Client(), "op");

        string[] recipients = ["team@example.com"];

        var created = await client.PostAsJsonAsync("/api/reports/subscriptions", new
        {
            recipients,
            frequency = "Daily",
            dayOfWeek = "7",
            hourLocal = 7,
            timeZoneId = "UTC",
            kind = "Alerts",
            isEnabled = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
    }

    [Fact]
    public async Task A_subscription_cannot_name_more_than_twenty_recipients()
    {
        Account("op", Role.Operator);
        var client = await SignedIn(Client(), "op");

        var recipients = Enumerable.Range(0, 21).Select(i => $"team{i}@example.com").ToArray();

        var created = await client.PostAsJsonAsync("/api/reports/subscriptions", new
        {
            recipients,
            frequency = "Daily",
            dayOfWeek = "Monday",
            hourLocal = 7,
            timeZoneId = "UTC",
            kind = "Alerts",
            isEnabled = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
    }

    [Fact]
    public async Task Editing_a_subscription_does_not_restore_an_older_send_status_over_a_dispatch_in_between()
    {
        // The update race: Update used to write the whole record back,
        // including the caller's stale LastSentUtc/LastError, so an edit
        // submitted around the same time as a dispatch could undo the
        // dispatcher's claim and cause a duplicate send. The store now keeps
        // its own copy of those two fields regardless of what the request
        // carries.
        Account("op", Role.Operator);
        var client = await SignedIn(Client(), "op");

        string[] oneRecipient = ["team@example.com"];

        var created = await client.PostAsJsonAsync("/api/reports/subscriptions", new
        {
            recipients = oneRecipient,
            frequency = "Daily",
            dayOfWeek = "Monday",
            hourLocal = 7,
            timeZoneId = "UTC",
            kind = "Alerts",
            isEnabled = true,
        });

        var subscription = await created.Content.ReadFromJsonAsync<Api.ReportSubscriptionView>(Json);
        Assert.NotNull(subscription);

        // Simulates the dispatcher claiming the window between the operator
        // reading the record and this edit landing.
        var store = _host.Services.GetRequiredService<IReportSubscriptionStore>();
        var dispatchedAt = DateTimeOffset.UtcNow;
        store.MarkDispatched(subscription!.Id, dispatchedAt);

        string[] twoRecipients = ["team@example.com", "second@example.com"];

        var edited = await client.PutAsJsonAsync($"/api/reports/subscriptions/{subscription.Id}", new
        {
            recipients = twoRecipients,
            frequency = "Daily",
            dayOfWeek = "Monday",
            hourLocal = 7,
            timeZoneId = "UTC",
            kind = "Alerts",
            isEnabled = true,
        });

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var view = await edited.Content.ReadFromJsonAsync<Api.ReportSubscriptionView>(Json);
        Assert.Equal(dispatchedAt, view!.LastSentUtc);
    }

    // --- the host itself ----------------------------------------------------

    [Fact]
    public async Task The_interface_is_served_from_the_same_origin()
    {
        // The failure that started this file: a host that cannot build its
        // route matcher answers every request with 500. Any page at all proves
        // the matcher was built.
        var response = await Client().GetAsync("/api/auth/state");

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}

/// <summary>
/// Program.cs with its stores in memory and its workers removed.
/// </summary>
/// <remarks>
/// Environment is "Testing" rather than the factory's default of Development,
/// because Development loads appsettings.Development.json — the operator's own
/// file, gitignored, carrying real vCenter addresses — and a test that reads it
/// behaves differently on every machine.
/// </remarks>
internal sealed class ObservatoryHost : WebApplicationFactory<Program>
{
    private readonly string _keyRing = Path.Combine(
        Path.GetTempPath(), "eo-smoke-keys-" + Guid.NewGuid().ToString("N"));

    public InMemoryUserAccountStore Accounts { get; } = new();

    public InMemoryComplianceStore Compliance { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Not a real credential and never sent anywhere: nothing here reaches
        // a database. Program.cs refuses to start without a password, and it
        // arrives from in-memory configuration, which the credential guard
        // rightly does not treat as a settings file.
        builder.UseSetting("Database:Password", "smoke-test-no-database-is-reached");
        builder.UseSetting("Storage:KeyRingPath", _keyRing);

        builder.ConfigureTestServices(services =>
        {
            Replace<IUserAccountStore>(services, Accounts);
            Replace<IEntityGraphStore>(services, new InMemoryEntityGraphStore());
            Replace<IAlertStateStore>(services, new InMemoryAlertStateStore());
            Replace<ICollectorHealthStore>(services, new InMemoryCollectorHealthStore());
            Replace<ICoverageStore>(services, new InMemoryCoverageStore());
            Replace<IEventStore>(services, new InMemoryEventStore());
            Replace<IObservationStore>(services, new InMemoryObservationStore());
            Replace<IMaintenanceWindowStore>(services, new InMemoryMaintenanceWindowStore());
            Replace<IComplianceStore>(services, Compliance);
            Replace<ISourceConnectionStore>(services, new TestConnectionStore());
            Replace<ISmtpSettingsStore>(services, new InMemorySmtpSettingsStore());
            Replace<IReportSubscriptionStore>(services, new InMemoryReportSubscriptionStore());
            Replace<IMailSender>(services, new NeverSendMailSender());

            // Only the product's own workers. Removing every IHostedService
            // would take the test server with it.
            foreach (var worker in services
                         .Where(d => d.ImplementationType == typeof(MonitoringWorker) ||
                                     d.ImplementationType == typeof(CompactionWorker) ||
                                     d.ImplementationType == typeof(ReportSchedulerWorker))
                         .ToList())
            {
                services.Remove(worker);
            }

            // Loud rather than silent. If anything still asks for the
            // database, a store was missed above, and a smoke suite that
            // quietly connected to whatever runs on 5432 would be testing the
            // developer's machine.
            services.RemoveAll<PostgresDatabase>();
            services.AddSingleton<PostgresDatabase>(_ => throw new InvalidOperationException(
                "The composition-root smoke suite reached PostgreSQL; a store was not replaced."));
        });
    }

    private static void Replace<T>(IServiceCollection services, T instance)
        where T : class
    {
        services.RemoveAll<T>();
        services.AddSingleton(instance);
    }

    /// <summary>
    /// The one IMailSender in this suite. Deliberately never reaches a network:
    /// a smoke suite that could actually send mail would be a smoke suite that
    /// occasionally does.
    /// </summary>
    private sealed class NeverSendMailSender : IMailSender
    {
        public Task<MailSendResult> SendAsync(
            SmtpSettings settings, OutgoingMail mail, CancellationToken cancellationToken) =>
            Task.FromResult(MailSendResult.Failed("No mail server is reachable from this test suite."));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        try
        {
            if (Directory.Exists(_keyRing))
            {
                Directory.Delete(_keyRing, recursive: true);
            }
        }
        catch (IOException)
        {
            // A key file still open at teardown is a temp directory left
            // behind, not a failed test.
        }
    }
}
