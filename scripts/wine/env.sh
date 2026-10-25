#!/usr/bin/env bash
# Source me:  . scripts/wine/env.sh   -- prepares a Proton/Wine environment for running win-x64 .NET apps and PE32 oracle EXEs.
# Variables (all optional):
#   BLITZCS_WINE_ROOT    Wine "files" directory containing bin/wine (default: Steam "Proton 11.0/files", also tries other Proton versions)
#   BLITZCS_WINE_PREFIX  prefix directory (default: $HOME/.cache/blitzcs/wineprefix)
#   BLITZCS_WINE_DISPLAY X display for Xvfb (default :97)
#   BLITZCS_WINE_WORK    short scratch directory for the published app copy (default /tmp/blitzcs-w-$UID)
_blitz_find_wine() {
  local c
  for c in "${BLITZCS_WINE_ROOT:-}" \
           "$HOME/.local/share/Steam/steamapps/common/Proton 11.0/files" \
           "$HOME/.steam/steam/steamapps/common/Proton 11.0/files" \
           "$HOME/.local/share/Steam/steamapps/common/Proton - Experimental/files" \
           "$HOME/.local/share/Steam/steamapps/common/Proton 10.0/files"; do
    [ -n "$c" ] && [ -x "$c/bin/wine" ] && { printf '%s' "$c"; return 0; }
  done
  return 1
}
if ! W=$(_blitz_find_wine); then
  echo "wine/env.sh: no Wine found; set BLITZCS_WINE_ROOT to a Wine 'files' directory (bin/wine inside)" >&2; return 1 2>/dev/null || exit 1
fi
export BLITZCS_WINE_ROOT="$W"
export WINEPREFIX="${BLITZCS_WINE_PREFIX:-$HOME/.cache/blitzcs/wineprefix}"
export WINEDEBUG=-all
export WINEDLLOVERRIDES="mshtml=d"          # do NOT disable mscoree: Wine needs it to load IL-only assemblies
export PATH="$W/bin:$PATH"
export LD_LIBRARY_PATH="$W/lib/x86_64-linux-gnu:$W/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
export DOTNET_gcServer=0 DOTNET_TieredPGO=0
export BLITZCS_WINE_WORK="${BLITZCS_WINE_WORK:-/tmp/blitzcs-w-$(id -u)}"
_d="${BLITZCS_WINE_DISPLAY:-:97}"
if [ -z "${BLITZCS_WINE_NO_XVFB:-}" ]; then
  if ! [ -e "/tmp/.X11-unix/X${_d#:}" ]; then
    _xv=$(command -v Xvfb || true); [ -z "$_xv" ] && [ -x "$HOME/.local/bin/Xvfb" ] && _xv="$HOME/.local/bin/Xvfb"
    if [ -n "$_xv" ]; then ("$_xv" "$_d" -screen 0 800x600x24 >/dev/null 2>&1 &) ; sleep 1; else echo "wine/env.sh: Xvfb not found (wineboot may still work headless)" >&2; fi
  fi
  export DISPLAY="$_d"
fi
mkdir -p "$WINEPREFIX" "$BLITZCS_WINE_WORK"
if [ ! -e "$WINEPREFIX/system.reg" ]; then
  echo "wine/env.sh: creating prefix $WINEPREFIX (first use)" >&2
  wineboot -i >/dev/null 2>&1 || wineboot -u >/dev/null 2>&1 || true
  wineserver -w 2>/dev/null || true
fi
# Opt-in (default unchanged): BLITZCS_WINE_GRAPHICS=1 copies the Wine build's vkd3d DLLs into the prefix when they are missing. Wine's ddraw loads wined3d, which imports
# libvkd3d-1.dll, and wineboot does not install it: without it LoadLibrary("ddraw.dll") fails in a 64-bit process (the host bridge of docs/binary-user32-bridge.md) and in a 32-bit one.
if [ -n "${BLITZCS_WINE_GRAPHICS:-}" ]; then
  for _arch in x86_64-windows:system32 i386-windows:syswow64; do
    _src="$BLITZCS_WINE_ROOT/lib/vkd3d/${_arch%%:*}"; _dst="$WINEPREFIX/drive_c/windows/${_arch##*:}"
    [ -d "$_src" ] && [ -d "$_dst" ] && for _f in "$_src"/libvkd3d*.dll; do [ -e "$_dst/$(basename "$_f")" ] || cp "$_f" "$_dst/"; done
  done
  unset _arch _src _dst _f
fi
# Opt-in (default unchanged): BLITZCS_WINE_AUDIO_DRIVER=pulse selects winepulse.drv, which honours PULSE_SINK. Proton-CachyOS otherwise loads
# winepipewire.drv, which ignores PULSE_SINK (see docs/wine-audio-routing.md).
if [ -n "${BLITZCS_WINE_AUDIO_DRIVER:-}" ]; then
  wine reg add 'HKCU\Software\Wine\Drivers' /v Audio /d "$BLITZCS_WINE_AUDIO_DRIVER" /f >/dev/null 2>&1 || echo "wine/env.sh: could not set the Wine audio driver to $BLITZCS_WINE_AUDIO_DRIVER" >&2
fi
unset -f _blitz_find_wine; unset W _d _xv
