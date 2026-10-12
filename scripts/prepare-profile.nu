def main [
  game_dir: path,
  profile_dir: path,
  --runtime-dir: path,
  --package: path,
  --videos-dir: string = '',
] {
  let game_dir = ($game_dir | path expand)
  let profile_dir = ($profile_dir | path expand)
  let runtime_dir = ($runtime_dir | path expand)
  let package_dir = ($package | path expand)
  let game_executable = ($game_dir | path join 'Lethal Company.exe')
  let runtime_bepinex = ($runtime_dir | path join 'BepInEx')
  let plugin_dir = ($profile_dir | path join 'BepInEx/plugins/TVRandomizer')

  if not ($game_executable | path exists) {
    error make {msg: $'Game executable not found: ($game_executable)'}
  }
  for relative in ['winhttp.dll' 'doorstop_config.ini' 'BepInEx/core/BepInEx.Preloader.dll' 'BepInEx/plugins/TVLoader/TVLoader.dll' 'BepInEx/plugins/LethalNetworkAPI/LethalNetworkAPI.dll' 'BepInEx/core/OdinSerializer/OdinSerializer.dll'] {
    if not (($runtime_dir | path join $relative) | path exists) {
      error make {msg: $'Required runtime file missing: ($relative)'}
    }
  }
  for assembly in ['TVRandomizer.dll' 'TVRandomizer.Core.dll' 'FSharp.Core.dll'] {
    let source = ($package_dir | path join $assembly)
    if not ($source | path exists) {
      error make {msg: $'Required mod assembly missing: ($source)'}
    }
  }
  if $videos_dir != '' and not (($videos_dir | path expand) | path exists) {
    error make {msg: $'Video directory not found: ($videos_dir)'}
  }
  if ($profile_dir | path exists) {
    error make {msg: $'Refusing to overwrite existing profile: ($profile_dir)'}
  }

  mkdir $profile_dir
  for entry in (ls $game_dir) {
    let basename = ($entry.name | path basename)
    if $basename not-in ['BepInEx' 'winhttp.dll' 'doorstop_config.ini' 'compatdata'] {
      let result = (do { ^ln -s $entry.name ($profile_dir | path join $basename) } | complete)
      if $result.exit_code != 0 { error make {msg: $result.stderr} }
    }
  }
  for relative in ['BepInEx' 'winhttp.dll' 'doorstop_config.ini'] {
    let result = (do { ^cp -R --no-preserve=mode,ownership ($runtime_dir | path join $relative) $profile_dir } | complete)
    if $result.exit_code != 0 { error make {msg: $result.stderr} }
  }
  mkdir $plugin_dir
  for assembly in ['TVRandomizer.dll' 'TVRandomizer.Core.dll' 'FSharp.Core.dll'] {
    let source = ($package_dir | path join $assembly)
    if not ($source | path exists) {
      error make {msg: $'Required mod assembly missing: ($source)'}
    }
    let result = (do { ^cp --no-preserve=mode,ownership $source $plugin_dir } | complete)
    if $result.exit_code != 0 { error make {msg: $result.stderr} }
  }
  mkdir ($profile_dir | path join 'compatdata')
  mkdir ($profile_dir | path join 'BepInEx/config')

  if $videos_dir != '' {
    let result = (do { ^ln -s ($videos_dir | path expand) ($plugin_dir | path join 'Television Videos') } | complete)
    if $result.exit_code != 0 { error make {msg: $result.stderr} }
  }

  print $'Prepared isolated profile at ($profile_dir)'
  print 'Game files are symlinked; BepInEx, mod assemblies, config, and Proton data are profile-local.'
}
