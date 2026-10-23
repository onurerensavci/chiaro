#!/usr/bin/env bash
# Chiaro.app (osx-arm64) paketleyici. Linux veya macOS'ta çalışır; imza için rcodesign (ad-hoc) gerekir.
# Kullanım: Packaging/build-mac.sh [çıktı_klasörü] [zip_yolu] [zip'e eklenecek ek dosyalar…]
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
PROJ="$(dirname "$HERE")"
OUT="${1:-$PROJ/artifacts}"
RCODESIGN="${RCODESIGN:-rcodesign}"
PUB="$OUT/publish-osx-arm64"
APP="$OUT/Chiaro.app"
rm -rf "$PUB" "$APP"
dotnet publish "$PROJ/ChiaroMac.csproj" -c Release -r osx-arm64 --self-contained -o "$PUB" -p:UseAppHost=true -p:PublishSingleFile=true
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUB/." "$APP/Contents/MacOS/"
rm -f "$APP/Contents/MacOS/"*.pdb
cp "$HERE/Info.plist" "$APP/Contents/Info.plist"
cp "$HERE/Chiaro.icns" "$APP/Contents/Resources/Chiaro.icns"
printf 'APPL????' > "$APP/Contents/PkgInfo"
chmod 755 "$APP/Contents/MacOS/Chiaro"
find "$APP/Contents/MacOS" -name '*.dylib' -exec chmod 755 {} +
# Tek dosya yayın: Contents/MacOS içinde yalnızca Mach-O dosyaları kalır (Chiaro + 3 dylib), böylece
# paket imzası (CodeResources) eksiksiz olur; .dll dosyaları MacOS klasöründe imzasız kalmaz.
# Ad-hoc imza: tüm iç Mach-O dosyaları + paket. Entitlements yalnızca ana yürütülebilir dosyaya.
# Sertleştirilmiş çalışma zamanı (runtime) bayrağı bilerek VERİLMEZ: .NET JIT'i allow-jit olmadan çalışmaz.
"$RCODESIGN" sign --entitlements-xml-file "Contents/MacOS/Chiaro:$HERE/Chiaro.entitlements" "$APP"
"$RCODESIGN" verify "$APP/Contents/MacOS/Chiaro" >/dev/null 2>&1 || echo "Uyarı: rcodesign verify ana dosyada uyarı verdi (ad-hoc imzada beklenir)."
echo "Hazır: $APP"
# İsteğe bağlı zip: Packaging/build-mac.sh <çıktı> <zip_yolu> [ek dosyalar…]
# zip -y sembolik bağları korur, Unix izinleri (çalıştırma biti) saklanır; Finder/Arşiv İzlencesi ve ditto -x -k ile açılır.
if [ $# -ge 2 ]; then
	ZIP="$(cd "$(dirname "$2")" && pwd)/$(basename "$2")"
	rm -f "$ZIP"
	(cd "$OUT" && zip -qry -X "$ZIP" Chiaro.app)
	shift 2
	for extra in "$@"; do (cd "$(dirname "$extra")" && zip -qX "$ZIP" "$(basename "$extra")"); done
	echo "Zip: $ZIP"
fi
