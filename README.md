# GroundControl

Runs xUnit.net v3 tests within a container of a Docker Compose project wherever they are started from: an IDE, the command line or a pipeline. Tests address services by their Docker Compose names, as they do within the project network, while the test runner, the results and the debugger stay on the host.

## How it works

GroundControl is an xUnit.net test framework. On the host, it serializes the test cases the runner asks to execute, starts a container on the network of the Docker Compose project (the test assembly's output directory is bind-mounted), and runs the same test assembly within it with those test cases. The results are reported back to the runner as those of the local test cases, so IDEs and `dotnet test` work as usual. When a debugger is attached to the test runner, GroundControl asks the IDE to attach it to the test process within the container.

## Usage

Reference the package from the test project and declare the test framework:

```xml
<ItemGroup>
    <PackageReference Include="GroundControl" Version="0.1.0" />
</ItemGroup>

<ItemGroup>
    <AssemblyAttribute Include="Xunit.TestFrameworkAttribute">
        <_Parameter1>GroundControl.GroundControlTestFramework</_Parameter1>
        <_Parameter1_TypeName>System.Type</_Parameter1_TypeName>
    </AssemblyAttribute>
</ItemGroup>
```

Start the Docker Compose project (`docker compose up --detach --build`) and run the tests as usual. The project is found by the closest Compose file in the directories of the test assembly, and its name is resolved as `docker compose` does (`COMPOSE_PROJECT_NAME`, `.env`, the Compose files).

## Settings

All optional, as assembly metadata of the test project (`<AssemblyMetadata Include="GroundControl.Service" Value="tests" />`) or environment variables (`GROUNDCONTROL_SERVICE=tests`):

| Setting | Default | Description |
| --- | --- | --- |
| `Project` | resolved by Docker Compose | Docker Compose project name. |
| `ProjectDirectory` | closest Compose file | Docker Compose project directory. |
| `Service` | a container per run | Docker Compose service running test processes, kept running (e.g., to keep debugger tools in it). |
| `Image` | .NET (or ASP.NET Core) image of the test assembly runtime | Image of the containers created for test runs. |
| `Network` | the one most services are on | Network of the containers created for test runs. |
| `ReadinessTimeout` | 5 minutes | How long to wait for the project to be ready: seconds, or a time span. |
| `Readiness` | `AllServices` | `AllServices`: every service is healthy (or running without a healthcheck) and one-off services have completed; `None`: no wait. |
| `Docker` | `docker` | Docker CLI executable. |
| `DockerContext` | current | Docker context. |
| `Debugger` | `rider` | Debugger integration: `rider` (the Rider plugin, then manual), `manual` (logs how to attach), or a type name implementing `IGroundControlDebugger`. |

## Debugging

Debug the tests as usual: the test process within the container waits for a debugger, and the IDE is asked to attach to it.

- **Rider**: install the plugin in `rider/` (`rider/build.ps1` builds it with the JDK bundled with Rider and installs it; restart Rider). Rider deploys its remote tools into the container on the first attach.
- **Other IDEs**: attach to the .NET process within the container by hand; the container and process ID are logged by the test run.

## Requirements

- .NET 8 or later, xUnit.net v3 (2.0 or later).
- Docker CLI on the host, with access to the Docker Compose project.

## Development

```
dotnet test
```

`test/GroundControl.Tests` holds unit tests and integration tests (trait `Category=Docker`). The integration tests start the Docker Compose project of `test/GroundControl.Sample` (an nginx service), run the sample tests through GroundControl from the host, and remove the project afterwards; they are skipped when Docker is not available. To run the unit tests only: `dotnet test --filter "Category!=Docker"`.
