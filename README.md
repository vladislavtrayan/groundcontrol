<!-- An absolute URL, so the logo shows on nuget.org too (it renders the README of the package). -->
<img src="https://raw.githubusercontent.com/vladislavtrayan/groundcontrol/main/assets/logo.svg" alt="GroundControl logo" width="96" height="96">

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

All optional, as assembly metadata of the test project (`<AssemblyMetadata Include="GroundControl.Network" Value="backend" />`) or environment variables (`GROUNDCONTROL_NETWORK=backend`):

| Setting | Default | Description |
| --- | --- | --- |
| `Project` | resolved by Docker Compose | Docker Compose project name. |
| `ProjectDirectory` | closest Compose file | Docker Compose project directory (relative to the directory of the test assembly). |
| `Image` | .NET (or ASP.NET Core) image of the test assembly runtime | Image of the tests containers; it needs `bash` (e.g., the Debian-based .NET images, not Alpine or chiseled ones). |
| `Network` | the one most services are on | Network of the tests containers. |
| `ReadinessTimeout` | 5 minutes | How long to wait for the project to be ready (every service healthy, or running without a healthcheck, and one-off services completed): seconds, or a time span; `0` not to wait. |
| `Docker` | `docker` | Docker CLI executable. |
| `DockerContext` | current | Docker context. |

## Debugging

Debug the tests as usual: the test process within the container waits for a debugger, and the IDE is asked to attach to it.

- **Rider**: install the plugin, `groundcontrol-rider-<version>.jar` of the [releases](https://github.com/vladislavtrayan/groundcontrol/releases), by *Settings → Plugins → ⚙ → Install Plugin from Disk*, and restart Rider (or build and install it from the sources by `rider/build.ps1`). Rider deploys its remote tools into the container on the first attach; they are kept in the Docker volume `groundcontrol-debugger-tools` for the containers of later runs (remove the volume after upgrading Rider if attaching fails).
- **Other IDEs**: attach to the .NET process within the container by hand; the container and process ID are logged by the test run.

## Requirements

- .NET 8 or later, xUnit.net v3 (2.0 or later).
- Docker CLI on the host, with access to the Docker Compose project.
- `bash` in the tests container (the test process is run by a bash script): the default .NET images have it.

## Development

```
dotnet test
```

`test/GroundControl.Tests` holds unit tests and integration tests (trait `Category=Docker`). The integration tests start the Docker Compose project of `test/GroundControl.Tests.Sample` (an nginx service), run the sample tests through GroundControl from the host, and remove the project afterwards; they are skipped when Docker is not available. To run the unit tests only: `dotnet test --filter "Category!=Docker"`.

### Releases

Pushing a version tag (e.g., `0.1.0`) runs the Release workflow: it builds the package and the Rider plugin of that version, publishes the package to nuget.org (trusted publishing, the nuget.org user in the `NUGET_USER` variable) and creates the GitHub release with both attached. Run manually, it builds both without publishing.

## License

[MIT](LICENSE)
