<!-- An absolute URL, so the logo shows on nuget.org too (it renders the README of the package). -->
<img src="https://raw.githubusercontent.com/vladislavtrayan/groundcontrol/main/assets/logo.svg" alt="GroundControl logo" width="96" height="96">

# GroundControl

Run your xUnit.net tests inside your Docker Compose project, straight from the IDE or `dotnet test`.

Your tests call services by their Compose names (`http://api`, `postgres:5432`), and services can call back into your tests. The test runner, the results and the debugger stay on your machine, so everything works as usual.

## Getting started

1. Add the package and switch the test framework in your test project:

    ```xml
    <ItemGroup>
        <PackageReference Include="GroundControl" Version="0.3.2" />
    </ItemGroup>

    <ItemGroup>
        <AssemblyAttribute Include="Xunit.TestFrameworkAttribute">
            <_Parameter1>GroundControl.GroundControlTestFramework</_Parameter1>
            <_Parameter1_TypeName>System.Type</_Parameter1_TypeName>
        </AssemblyAttribute>
    </ItemGroup>
    ```

2. Declare the `groundcontrol` service in your Compose file, with the .NET runtime your tests target and the directory that contains your test assemblies mounted:

    ```yaml
    services:
      groundcontrol:
        image: mcr.microsoft.com/dotnet/aspnet:10.0
        command: [ "sleep", "infinity" ]
        volumes:
          - .:/workspace:ro
    ```

3. Start your Compose project:

    ```bash
    docker compose up --detach --build
    ```

4. Run the tests as you normally do.

GroundControl finds the project by the closest `compose.yaml` above the test assembly.

You need .NET 8 or later, xUnit.net v3, and Docker.

## How it works

GroundControl runs your test assembly inside the `groundcontrol` service of your project, which is up from `docker compose up` until `docker compose down`. The results come back to your runner as if the tests ran locally.

Test runs share that container, the way they would share your machine: when several test projects run at once, they all run in it side by side. GroundControl never creates, replaces or removes it.

Before running, GroundControl checks the service: it needs the runtime your tests target (GroundControl tells you which image to use otherwise), `sh`, and a mount of the directory of your test assemblies at any path. Then it waits until your services are healthy and one-off jobs such as migrations have finished; if the `groundcontrol` service isn't running, it asks you to start it with `docker compose up -d groundcontrol`.

## Calling your tests from services

The tests are reachable as `groundcontrol` on the project network, from `docker compose up` on. If a test fixture hosts a web server, services can call it at `http://groundcontrol:<port>`.

Two things to keep in mind:

- Listen on all interfaces (`http://*:8080` or `0.0.0.0`), not on `localhost`.
- Read the name from the `GROUNDCONTROL_CONTAINER` environment variable rather than hard-coding it.

As on your machine, test projects running at the same time must not listen on the same port. Projects in parallel pipelines get their own container and don't affect each other.

If a test run's host process dies, its tests are stopped within about 30 seconds, so they don't keep holding ports.

## Settings

All settings are optional. Set them as assembly metadata in the test project:

```xml
<ItemGroup>
    <AssemblyMetadata Include="GroundControl.Network" Value="backend" />
</ItemGroup>
```

Or as environment variables, which win over metadata: `GROUNDCONTROL_NETWORK=backend`.

| Setting | What it does | Default |
| --- | --- | --- |
| `Project` | Compose project name | found by the project directory |
| `ProjectDirectory` | Compose project directory, relative to the test assembly | closest `compose.yaml` |
| `Container` | The tests service, which services reach the tests by | `groundcontrol` |
| `Network` | Network the tests service must be on | the one most services use |
| `ReadinessTimeout` | How long to wait for the project, in seconds or `hh:mm:ss`; `0` skips the wait | 5 minutes |
| `DockerHost` | Docker Engine endpoint, e.g., `tcp://build:2375` | `DOCKER_HOST` |
| `DockerContext` | Docker CLI context to use | the current one |

## Debugging

Start debugging as usual. The test process in the container waits for the debugger.

- **Rider**: install the GroundControl plugin, `groundcontrol-rider-<version>.jar` from the [releases](https://github.com/vladislavtrayan/groundcontrol/releases), via *Settings → Plugins → ⚙ → Install Plugin from Disk*. Rider then attaches on its own. The first attach takes a little longer while Rider copies its debugger tools; they stay while the container runs. Mounting a volume at `/root/.RiderDebuggerTools` in the `groundcontrol` service is optional, so they survive recreating it.
- **Other IDEs**: attach to the process by hand. The debug output shows the container and process ID.

## Development

`dotnet test` runs the unit tests and, if Docker is available, the integration tests against the sample project in `test/GroundControl.Tests.Sample`, which declares the tests service in `compose.groundcontrol.yaml`.

`rider/build.ps1` builds the Rider plugin and installs it into your local Rider.

Pushing a version tag such as `0.3.2` publishes the package to nuget.org and creates a GitHub release with the plugin.

## License

[MIT](LICENSE)
