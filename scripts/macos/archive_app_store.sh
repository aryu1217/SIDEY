#!/bin/sh
set -eu

SIDEY_REPO_ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && /bin/pwd -P)
python3 "$SIDEY_REPO_ROOT/scripts/macos/verify_content_assets.py"
SIDEY_APP_STORE_VERIFIER_URL=${SIDEY_APP_STORE_VERIFIER_URL:-}
SIDEY_DEVELOPMENT_TEAM=${SIDEY_DEVELOPMENT_TEAM:-}
python3 "$SIDEY_REPO_ROOT/scripts/sidey_version.py" --check macos
SIDEY_EXPECTED_MARKETING_VERSION=$(python3 "$SIDEY_REPO_ROOT/scripts/sidey_version.py" --get macVersion)
SIDEY_EXPECTED_BUILD_VERSION=$(python3 "$SIDEY_REPO_ROOT/scripts/sidey_version.py" --get macBuild)
SIDEY_ARCHIVE_PATH=${1:-$SIDEY_REPO_ROOT/build/app-store/SIDEY.xcarchive}
SIDEY_DERIVED_DATA=${SIDEY_DERIVED_DATA:-$SIDEY_REPO_ROOT/build/app-store-derived}

if [ -z "$SIDEY_APP_STORE_VERIFIER_URL" ]; then
	echo "SIDEY_APP_STORE_VERIFIER_URL is required" >&2
	exit 64
fi
case "$SIDEY_APP_STORE_VERIFIER_URL" in
	https://*) ;;
	*)
		echo "SIDEY_APP_STORE_VERIFIER_URL must use HTTPS" >&2
		exit 64
		;;
esac
case "$SIDEY_APP_STORE_VERIFIER_URL" in
	*"@"*|*"?"*|*"#"*)
		echo "SIDEY_APP_STORE_VERIFIER_URL must not contain credentials, a query, or a fragment" >&2
		exit 64
		;;
esac
if [ -z "$SIDEY_DEVELOPMENT_TEAM" ]; then
	echo "SIDEY_DEVELOPMENT_TEAM is required for App Store signing" >&2
	exit 64
fi

# Platform mirrors are covered by the macOS asset tests. Keep this archive
# preflight on canonical sources so it never validates Windows implementation.
python3 "$SIDEY_REPO_ROOT/scripts/validate_pixel_assets.py" --canonical-only
mkdir -p "$(dirname -- "$SIDEY_ARCHIVE_PATH")" "$SIDEY_DERIVED_DATA"

set --
if [ -n "${SIDEY_CLONED_SOURCE_PACKAGES:-}" ]; then
    set -- -clonedSourcePackagesDirPath "$SIDEY_CLONED_SOURCE_PACKAGES"
fi

xcodebuild \
	-project "$SIDEY_REPO_ROOT/macos/SIDEY.xcodeproj" \
	-scheme SIDEY \
	-configuration Release \
	-destination 'generic/platform=macOS' \
	ONLY_ACTIVE_ARCH=NO \
	-derivedDataPath "$SIDEY_DERIVED_DATA" \
	-archivePath "$SIDEY_ARCHIVE_PATH" \
	-disableAutomaticPackageResolution \
	-allowProvisioningUpdates \
	"DEVELOPMENT_TEAM=$SIDEY_DEVELOPMENT_TEAM" \
	"SIDEY_APP_STORE_VERIFIER_URL=$SIDEY_APP_STORE_VERIFIER_URL" \
	"$@" \
	archive

SIDEY_APP="$SIDEY_ARCHIVE_PATH/Products/Applications/SIDEY.app"
SIDEY_EXECUTABLE="$SIDEY_APP/Contents/MacOS/SIDEY"
SIDEY_INFO_PLIST="$SIDEY_APP/Contents/Info.plist"
SIDEY_PRIVACY_MANIFEST="$SIDEY_APP/Contents/Resources/PrivacyInfo.xcprivacy"
SIDEY_DSYM_DWARF="$SIDEY_ARCHIVE_PATH/dSYMs/SIDEY.app.dSYM/Contents/Resources/DWARF/SIDEY"

for SIDEY_REQUIRED_PATH in \
	"$SIDEY_EXECUTABLE" \
	"$SIDEY_INFO_PLIST" \
	"$SIDEY_PRIVACY_MANIFEST" \
	"$SIDEY_DSYM_DWARF"; do
	if [ ! -e "$SIDEY_REQUIRED_PATH" ]; then
		echo "Required App Store archive file missing: $SIDEY_REQUIRED_PATH" >&2
		exit 1
	fi
done

python3 "$SIDEY_REPO_ROOT/scripts/macos/verify_binary_compatibility.py" "$SIDEY_APP"

SIDEY_EXECUTABLE_UUIDS=$(xcrun dwarfdump --uuid "$SIDEY_EXECUTABLE" | awk '{print $2}' | sort)
SIDEY_DSYM_UUIDS=$(xcrun dwarfdump --uuid "$SIDEY_DSYM_DWARF" | awk '{print $2}' | sort)
if [ -z "$SIDEY_EXECUTABLE_UUIDS" ] || [ "$SIDEY_EXECUTABLE_UUIDS" != "$SIDEY_DSYM_UUIDS" ]; then
	echo "App Store dSYM UUIDs do not match the executable" >&2
	exit 1
fi

if [ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$SIDEY_INFO_PLIST")" != "app.sidey.desktop.appstore" ]; then
	echo "Unexpected App Store bundle identifier" >&2
	exit 1
fi
if [ "$(/usr/libexec/PlistBuddy -c 'Print :SIDEYReleaseChannel' "$SIDEY_INFO_PLIST")" != "app-store" ]; then
	echo "App Store archive must use the app-store release channel" >&2
	exit 1
fi
if [ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$SIDEY_INFO_PLIST")" != "$SIDEY_EXPECTED_MARKETING_VERSION" ]; then
	echo "Unexpected App Store marketing version" >&2
	exit 1
fi
if [ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$SIDEY_INFO_PLIST")" != "$SIDEY_EXPECTED_BUILD_VERSION" ]; then
	echo "Unexpected App Store build version" >&2
	exit 1
fi
if [ "$(/usr/libexec/PlistBuddy -c 'Print :SIDEYAppStoreVerifierURL' "$SIDEY_INFO_PLIST")" != "$SIDEY_APP_STORE_VERIFIER_URL" ]; then
	echo "App Store verifier URL was not embedded correctly" >&2
	exit 1
fi
if /usr/libexec/PlistBuddy -c 'Print :CFBundleURLTypes' "$SIDEY_INFO_PLIST" >/dev/null 2>&1; then
	echo "App Store archive must not declare the direct-distribution OAuth URL scheme" >&2
	exit 1
fi
if /usr/libexec/PlistBuddy -c 'Print :SUFeedURL' "$SIDEY_INFO_PLIST" >/dev/null 2>&1; then
	echo "App Store archive must not include Sparkle configuration" >&2
	exit 1
fi
if [ -e "$SIDEY_APP/Contents/Frameworks/Sparkle.framework" ]; then
	echo "App Store archive must not bundle Sparkle" >&2
	exit 1
fi
if [ -e "$SIDEY_APP/Contents/Library/LoginItems" ]; then
	echo "App Store archive must not bundle the direct-distribution login helper" >&2
	exit 1
fi

# App Store ingestion rejects quarantine even when code signing is valid (91109).
SIDEY_ARCHIVE_XATTRS=$(/usr/bin/xattr -r "$SIDEY_APP")
if printf '%s\n' "$SIDEY_ARCHIVE_XATTRS" | grep -E ': com\.apple\.quarantine$' >/dev/null; then
	echo "App Store archive still contains com.apple.quarantine; do not upload" >&2
	exit 1
fi

codesign --verify --deep --strict "$SIDEY_APP"
if otool -L "$SIDEY_EXECUTABLE" | grep -F 'Sparkle.framework' >/dev/null; then
	echo "App Store executable must not link Sparkle" >&2
	exit 1
fi

echo "Verified Mac App Store archive: $SIDEY_ARCHIVE_PATH"
