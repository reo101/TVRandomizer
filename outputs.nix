inputs:
inputs.flake-parts.lib.mkFlake { inherit inputs; } (
  { lib, ... }:
  {
    systems = lib.filter (system: system != "x86_64-darwin") (import inputs.systems.outPath);
    imports = [ inputs.flake-file.flakeModules.default ];
    flake-file = {
      nixConfig = {
        commit-lockfile-summary = "chore(flake): update `flake.lock`";
        extra-experimental-features = [ "pipe-operators" ];
      };
      inputs = {
        systems.url = "github:nix-systems/default";
        nixpkgs.url = "github:nixos/nixpkgs/nixos-unstable";
        flake-file.url = "github:vic/flake-file";
        flake-parts = {
          url = "github:hercules-ci/flake-parts";
          inputs.nixpkgs-lib.follows = "nixpkgs";
        };
      };
    };
    debug = true;
    perSystem =
      { pkgs, config, ... }:
      let
        version = "1.1.0";
        rid = pkgs.dotnetCorePackages.systemToDotnetRid pkgs.stdenv.hostPlatform.system;
        source = lib.fileset.toSource {
          root = ./.;
          fileset = lib.fileset.unions [
            ./NuGet.Config
            ./global.json
            (lib.fileset.fileFilter (file: file.hasExt "fs" || file.hasExt "fsproj") ./TVRandomizer)
            (lib.fileset.fileFilter (file: file.hasExt "fs" || file.hasExt "fsproj") ./TVRandomizer.Core)
            (lib.fileset.fileFilter (file: file.hasExt "fs" || file.hasExt "fsproj") ./TVRandomizer.Core.Tests)
            (lib.fileset.fileFilter (
              file: file.hasExt "fs" || file.hasExt "fsproj"
            ) ./TVRandomizer.Contracts.Tests)
            (lib.fileset.fileFilter (
              file: file.hasExt "fs" || file.hasExt "fsproj"
            ) ./TVRandomizer.Playback.Tests)
          ];
        };
        dotnetArguments = {
          src = source;
          inherit version;
          nugetDeps = ./deps.json;
          dotnet-sdk = pkgs.dotnet-sdk_10;
          dotnet-runtime = pkgs.dotnet-runtime_10;
          executables = [ ];
          useAppHost = false;
        };
        archive =
          team: name: release: hash:
          pkgs.fetchurl {
            url = "https://thunderstore.io/package/download/${team}/${name}/${release}/";
            name = "${name}-${release}.zip";
            inherit hash;
          };
        loaderArchive =
          archive "Rattenbonkers" "TVLoader" "1.1.2"
            "sha256-i2UNXp2rcBfUS0WGA8hRinX++IWHBgbFqr+nH3aF7Ok=";
        apiArchive =
          archive "xilophor" "LethalNetworkAPI" "3.4.2"
            "sha256-i91eBMFH471ks1p6fbzJN5wvxKXniSIXxI6OYFeny5s=";
        odinArchive =
          archive "Lordfirespeed" "OdinSerializer" "2024.2.2700"
            "sha256-MCRG0hkZBqCpi0F5rrjRHZ2wNhe2HKw1iGsD+K/qEnM=";
        bepArchive =
          archive "BepInEx" "BepInExPack" "5.4.2100"
            "sha256-B5KwSsGKcJSKz6ovqQBXJICcFFOKWhe3Zf3itib2xxA=";
        runtime = pkgs.runCommand "tvrandomizer-test-runtime" { nativeBuildInputs = [ pkgs.unzip ]; } ''
          mkdir -p "$out/BepInEx/plugins/TVLoader"
          unzip -q ${bepArchive} -d base
          cp -r base/BepInExPack/. "$out/"
          unzip -q ${loaderArchive} -d loader
          cp loader/TVLoader.dll "$out/BepInEx/plugins/TVLoader/"
          unzip -q ${apiArchive} 'BepInEx/*' -d "$out"
          unzip -q ${odinArchive} 'BepInEx/*' -d "$out"
        '';
        dependencies = pkgs.buildDotnetModule (
          dotnetArguments
          // {
            pname = "tvrandomizer-dependencies";
            projectFile = [
              "TVRandomizer/TVRandomizer.fsproj"
              "TVRandomizer.Core.Tests/TVRandomizer.Core.Tests.fsproj"
              "TVRandomizer.Contracts.Tests/TVRandomizer.Contracts.Tests.fsproj"
              "TVRandomizer.Playback.Tests/TVRandomizer.Playback.Tests.fsproj"
            ];
          }
        );
        contracts = pkgs.buildDotnetModule (
          dotnetArguments
          // {
            pname = "TVRandomizer.Contracts.Tests";
            projectFile = "TVRandomizer.Contracts.Tests/TVRandomizer.Contracts.Tests.fsproj";
            executables = [ "TVRandomizer.Contracts.Tests" ];
            useAppHost = true;
            meta.mainProgram = "TVRandomizer.Contracts.Tests";
          }
        );
      in
      {
        packages.default = pkgs.buildDotnetModule (
          dotnetArguments
          // {
            pname = "TVRandomizer";
            projectFile = "TVRandomizer/TVRandomizer.fsproj";
            installPhase = ''
              runHook preInstall
              mkdir -p "$out"
              for assembly in TVRandomizer.dll TVRandomizer.Core.dll FSharp.Core.dll; do
                install -m 644 "TVRandomizer/bin/Release/netstandard2.1/${rid}/$assembly" "$out/"
              done
              runHook postInstall
            '';
          }
        );
        packages.tests = pkgs.buildDotnetModule (
          dotnetArguments
          // {
            pname = "TVRandomizer.Core.Tests";
            projectFile = "TVRandomizer.Core.Tests/TVRandomizer.Core.Tests.fsproj";
            useAppHost = true;
            executables = [ "TVRandomizer.Core.Tests" ];
            doCheck = true;
            checkPhase = ''
              runHook preCheck
              dotnet TVRandomizer.Core.Tests/bin/Release/net10.0/${rid}/TVRandomizer.Core.Tests.dll
              runHook postCheck
            '';
          }
        );
        packages.contracts = contracts;
        packages.test-runtime = runtime;
        packages.zip =
          pkgs.runCommand "Lesnomievi-TVRandomizer-${version}.zip"
            {
              nativeBuildInputs = [
                pkgs.resvg
                pkgs.zip
                pkgs.findutils
              ];
            }
            ''
              export LC_ALL=C TZ=UTC
              mkdir package
              cd package
              mkdir -p BepInEx/plugins/TVRandomizer
              cp ${config.packages.default}/*.dll BepInEx/plugins/TVRandomizer/
              cp ${./README.md} README.md
              cp ${./LICENSE} LICENSE
              cp ${./packaging/manifest.json} manifest.json
              cp ${./packaging/FSharp.Core.LICENSE} FSharp.Core.LICENSE
              resvg ${./packaging/icon.svg} icon.png
              find . -type f -exec touch -t 198001010000.00 {} +
              find . -type f -printf '%P\n' | sort | zip -X -9 "$out" -@
            '';
        checks.playback = pkgs.buildDotnetModule (
          dotnetArguments
          // {
            pname = "TVRandomizer.Playback.Tests";
            projectFile = "TVRandomizer.Playback.Tests/TVRandomizer.Playback.Tests.fsproj";
            doCheck = true;
            checkPhase = ''
              runHook preCheck
              dotnet TVRandomizer.Playback.Tests/bin/Release/net10.0/${rid}/TVRandomizer.Playback.Tests.dll
              runHook postCheck
            '';
          }
        );
        checks.core = config.packages.tests;
        checks.contracts = pkgs.runCommand "tvrandomizer-contract-checks" { } ''
          ${lib.getExe contracts} ${runtime}/BepInEx/plugins/TVLoader/TVLoader.dll ${runtime}/BepInEx/plugins/LethalNetworkAPI/LethalNetworkAPI.dll ${config.packages.zip} ${config.packages.default}/TVRandomizer.dll
          touch "$out"
        '';
        checks.profile =
          pkgs.runCommand "tvrandomizer-profile-check"
            {
              nativeBuildInputs = [
                pkgs.nushell
                pkgs.coreutils
              ];
            }
            ''
              mkdir -p game/Lethal\ Company_Data/Managed
              touch "game/Lethal Company.exe"
              nu ${./scripts/prepare-profile.nu} "$PWD/game" "$PWD/profile" --runtime-dir ${runtime} --package ${config.packages.default}
              test -L "profile/Lethal Company.exe"
              test -f profile/winhttp.dll
              test -f profile/doorstop_config.ini
              test -f profile/BepInEx/plugins/TVLoader/TVLoader.dll
              test -f profile/BepInEx/plugins/LethalNetworkAPI/LethalNetworkAPI.dll
              test -f profile/BepInEx/core/OdinSerializer/OdinSerializer.dll
              test -f profile/BepInEx/plugins/TVRandomizer/FSharp.Core.dll
              test ! -e game/BepInEx
              touch "$out"
            '';
        devShells.default = pkgs.mkShell {
          packages = [
            pkgs.dotnet-sdk_10
            pkgs.nushell
            pkgs.fsautocomplete
            pkgs.fantomas
          ];
        };
        apps.update-deps = {
          type = "app";
          program = lib.getExe (
            pkgs.writeShellScriptBin "update-tvrandomizer-dependencies" ''
              ${dependencies.fetch-deps} ./deps.json
            ''
          );
        };
        apps.prepare-profile = {
          type = "app";
          program = lib.getExe (
            pkgs.writeShellApplication {
              name = "prepare-tvrandomizer-profile";
              runtimeInputs = [
                pkgs.nushell
                pkgs.coreutils
              ];
              text = ''
                exec nu ${./scripts/prepare-profile.nu} --runtime-dir ${runtime} --package ${config.packages.default} "$@"
              '';
            }
          );
        };
        apps.launch-profile = {
          type = "app";
          program = lib.getExe (
            pkgs.writeShellApplication {
              name = "launch-tvrandomizer-profile";
              runtimeInputs = [ pkgs.nushell ];
              text = ''
                exec nu ${./scripts/launch-profile.nu} "$@"
              '';
            }
          );
        };
      };
  }
)
