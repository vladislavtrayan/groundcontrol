<!-- An absolute URL, so the logo shows on nuget.org too (it renders the README of the package). -->
<img src="https://raw.githubusercontent.com/vladislavtrayan/groundcontrol/main/assets/logo.svg" alt="GroundControl logo" width="96" height="96">

# GroundControl

Runs xUnit.net v3 tests inside a container of your Docker Compose project, whether started from an IDE, `dotnet test` or a pipeline. Tests reach services by their Compose names, as if they were a service themselves, while the runner, the results and the debugger stay on the host.

## Usage

Reference the package and declare the test framework in the test project:

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

Start the project (`docker compose up --detach --build`) and run the tests as usual. The project is the closest Compose file above the test assembly.

Requirements: .NET 8 or later, xUnit.net v3 2.0 or later, and the Docker CLI with access to the project.

## How it works

For each test run, GroundControl starts a container on the project network with the project directory mounted read-only, and runs the same test assembly in it with the test cases the runner asked for. Results come back as those of the local test cases, so IDEs and `dotnet test` work as usual. The container is removed when the run ends.

## Settings

Optional, as assembly metadata (`<AssemblyMetadata Include="GroundControl.Network" Value="backend" />`) or environment variables (`GROUNDCONTROL_NETWORK=backend`):

| Setting | Default | Description |
| --- | --- | --- |
| `Project` | resolved by Compose | Compose project name. |
| `ProjectDirectory` | closest Compose file | Compose project directory, relative to the test assembly. |
| `Image` | .NET or ASP.NET Core image of the test runtime | Image of the tests container; it needs `bash`, as the default .NET images have (Alpine and chiseled ones do not). |
| `Network` | the one most services are on | Network of the tests container. |
| `ReadinessTimeout` | 5 minutes | How long to wait for every service to be healthy (or running, without a healthcheck) and one-off services to complete, in seconds or as a time span; `0` to not wait. |
| `Docker` | `docker` | Docker CLI executable. |
| `DockerContext` | current | Docker context. |

## Debugging

Debug the tests as usual: the test process in the container waits for a debugger, and the IDE is asked to attach.

- **Rider**: install `groundcontrol-rider-<version>.jar` from the [releases](https://github.com/vladislavtrayan/groundcontrol/releases) by *Settings → Plugins → ⚙ → Install Plugin from Disk* and restart Rider. Rider deploys its debugger tools into the container on the first attach; they are kept in the `groundcontrol-debugger-tools` Docker volume for later runs (remove it if attaching fails after a Rider upgrade).
- **Other IDEs**: attach to the process in the container by hand; the run logs the container and process ID.

## Development

`dotnet test` runs the unit tests and, when Docker is available, the integration tests (trait `Category=Docker`), which run the sample project in `test/GroundControl.Tests.Sample` through GroundControl against its Compose project. `rider/build.ps1` builds the Rider plugin and installs it into the local Rider.

Pushing a version tag such as `0.1.0` builds the package and the plugin, publishes the package to nuget.org and creates the GitHub release with both.

## License

[MIT](LICENSE)
