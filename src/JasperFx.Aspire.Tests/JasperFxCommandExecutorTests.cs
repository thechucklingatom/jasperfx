using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using JasperFx.Aspire;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace JasperFx.Aspire.Tests;

public class JasperFxCommandExecutorTests
{
    private static readonly Dictionary<string, string> NoEnv = new();

    // A minimal concrete resource so the environment-resolution tests don't need a full AppHost.
    private sealed class TestResource(string name) : Resource(name);

    // Reproduces GH #560: Aspire's own environment callbacks (e.g. the endpoint/reference population
    // registered by WithReference) read EnvironmentCallbackContext.Resource. When the executor built
    // the callback context without the resource, that getter threw
    // "Resource is not set. This callback context is not associated with a resource.",
    // the resolution was abandoned, and the child process launched with none of the Aspire-managed
    // environment (connection strings, service discovery), so Critter apps failed at startup.
    [Fact]
    public async Task resolve_environment_associates_the_resource_with_the_callback_context()
    {
        var resource = new TestResource("server-eyzqhsqj");
        resource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
        {
            // Same access pattern as Aspire.Hosting's CreateEndpointReferenceEnvironmentPopulationCallback.
            context.EnvironmentVariables[$"services__{context.Resource.Name}__http__0"] = "http://localhost:5000";
            return Task.CompletedTask;
        }));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

        var environment = await JasperFxCommandExecutor.ResolveEnvironmentAsync(
            resource, executionContext, NullLogger.Instance, CancellationToken.None);

        environment["services__server-eyzqhsqj__http__0"].ShouldBe("http://localhost:5000");
    }

    // Straightforward WithEnvironment values (no resource association needed) must still come through.
    [Fact]
    public async Task resolve_environment_captures_plain_environment_values()
    {
        var resource = new TestResource("api");
        resource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
        {
            context.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development";
            return Task.CompletedTask;
        }));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

        var environment = await JasperFxCommandExecutor.ResolveEnvironmentAsync(
            resource, executionContext, NullLogger.Instance, CancellationToken.None);

        environment["ASPNETCORE_ENVIRONMENT"].ShouldBe("Development");
    }

    // A failing callback (e.g. a WithReference whose endpoint can't be evaluated) must not throw away
    // the environment contributed by the other, healthy callbacks — the GH #560 regression.
    [Fact]
    public async Task resolve_environment_keeps_healthy_values_when_one_callback_fails()
    {
        var resource = new TestResource("api");
        resource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
        {
            context.EnvironmentVariables["ConnectionStrings__postgres"] = "Host=localhost;Port=5432";
            return Task.CompletedTask;
        }));
        resource.Annotations.Add(new EnvironmentCallbackAnnotation(_ =>
            throw new InvalidOperationException("boom")));
        resource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
        {
            context.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development";
            return Task.CompletedTask;
        }));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

        var environment = await JasperFxCommandExecutor.ResolveEnvironmentAsync(
            resource, executionContext, NullLogger.Instance, CancellationToken.None);

        environment["ConnectionStrings__postgres"].ShouldBe("Host=localhost;Port=5432");
        environment["ASPNETCORE_ENVIRONMENT"].ShouldBe("Development");
    }

    // Cancellation is not a resolution failure to swallow — it must propagate so the command reports
    // Canceled rather than silently launching the child with a partial environment.
    [Fact]
    public async Task resolve_environment_propagates_cancellation()
    {
        var resource = new TestResource("api");
        using var cts = new CancellationTokenSource();
        resource.Annotations.Add(new EnvironmentCallbackAnnotation(_ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            JasperFxCommandExecutor.ResolveEnvironmentAsync(
                resource, executionContext, NullLogger.Instance, cts.Token));
    }

    [Fact]
    public void build_start_info_runs_dotnet_run_with_no_build_and_the_verb()
    {
        var psi = JasperFxCommandExecutor.BuildStartInfo(
            "/code/api/Api.csproj", "/code/api", "check-env", null, NoEnv);

        psi.FileName.ShouldBe("dotnet");
        psi.WorkingDirectory.ShouldBe("/code/api");
        psi.ArgumentList.ShouldBe(["run", "--project", "/code/api/Api.csproj", "--no-build", "--", "check-env"]);
        psi.RedirectStandardOutput.ShouldBeTrue();
        psi.RedirectStandardError.ShouldBeTrue();
        psi.UseShellExecute.ShouldBeFalse();
    }

    [Fact]
    public void build_start_info_appends_fixed_arguments()
    {
        var psi = JasperFxCommandExecutor.BuildStartInfo(
            "/code/api/Api.csproj", "/code/api", "resources", "setup", NoEnv);

        psi.ArgumentList.ShouldBe(
            ["run", "--project", "/code/api/Api.csproj", "--no-build", "--", "resources", "setup"]);
    }

    [Fact]
    public void build_start_info_splits_multi_token_arguments()
    {
        var psi = JasperFxCommandExecutor.BuildStartInfo(
            "/code/api/Api.csproj", "/code/api", "projections", "rebuild Orders", NoEnv);

        psi.ArgumentList.ShouldBe(
            ["run", "--project", "/code/api/Api.csproj", "--no-build", "--", "projections", "rebuild", "Orders"]);
    }

    [Fact]
    public void build_start_info_applies_resolved_environment()
    {
        var env = new Dictionary<string, string>
        {
            ["ConnectionStrings__postgres"] = "Host=localhost;Port=5432",
            ["ASPNETCORE_ENVIRONMENT"] = "Development"
        };

        var psi = JasperFxCommandExecutor.BuildStartInfo(
            "/code/api/Api.csproj", "/code/api", "check-env", null, env);

        psi.Environment["ConnectionStrings__postgres"].ShouldBe("Host=localhost;Port=5432");
        psi.Environment["ASPNETCORE_ENVIRONMENT"].ShouldBe("Development");
    }

    [Fact]
    public void map_result_success_on_zero_exit_code()
    {
        var result = JasperFxCommandExecutor.MapResult("check-env", 0, "all good");

        result.Success.ShouldBeTrue();
    }

    [Fact]
    public void map_result_failure_on_nonzero_exit_code_includes_verb_code_and_tail()
    {
        var result = JasperFxCommandExecutor.MapResult("resources", 1, "could not connect to database");

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("resources");
        result.Message.ShouldContain("1");
        result.Message.ShouldContain("could not connect to database");
    }

    [Fact]
    public void map_result_failure_with_no_output_still_reports_the_exit_code()
    {
        var result = JasperFxCommandExecutor.MapResult("describe", 3, "");

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("3");
    }
}
