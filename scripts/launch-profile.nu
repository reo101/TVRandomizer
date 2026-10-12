def main [profile_dir: path, proton: path, steam_dir: path, ...arguments: string] {
  let profile_dir = ($profile_dir | path expand)
  let proton = ($proton | path expand)
  let executable = ($profile_dir | path join 'Lethal Company.exe')
  let compatibility_data = ($profile_dir | path join 'compatdata')

  if not ($executable | path exists) {
    error make {msg: $'Profile game executable not found: ($executable)'}
  }
  if not ($proton | path exists) {
    error make {msg: $'Proton launcher not found: ($proton)'}
  }

  if not (($profile_dir | path join 'winhttp.dll') | path exists) {
    error make {msg: 'Profile is missing the Doorstop loader.'}
  }
  $env.STEAM_COMPAT_DATA_PATH = $compatibility_data
  $env.STEAM_COMPAT_CLIENT_INSTALL_PATH = ($steam_dir | path expand)
  $env.SteamAppId = '1966720'
  $env.SteamGameId = '1966720'
  $env.WINEDLLOVERRIDES = 'winhttp=n,b'
  cd $profile_dir
  let result = (do { ^$proton run $executable ...$arguments } | complete)
  print $result.stdout
  print -e $result.stderr
  exit $result.exit_code
}
