"""Load and validate SIDEY's desired App Store Connect state."""

from __future__ import annotations

from dataclasses import dataclass
import json
from pathlib import Path
import re
from typing import Any
from urllib.parse import urlparse

try:
    from commerce_localizations import (
        app_store_display_name,
        read_source as read_commerce_source,
    )
except ModuleNotFoundError:  # Imported as scripts.app_store_connect.model.
    from scripts.commerce_localizations import (
        app_store_display_name,
        read_source as read_commerce_source,
    )

APP_STORE_LOCALES = ("ko", "en-US", "en-GB", "en-CA", "en-AU", "ja", "zh-Hant")
COMMERCE_LOCALES = ("ko", "en", "ja", "zh-Hant")
ENGLISH_APP_STORE_LOCALES = ("en-US", "en-GB", "en-CA", "en-AU")
TARGET_TERRITORIES = (
    "KOR",
    "USA",
    "CAN",
    "GBR",
    "AUS",
    "SGP",
    "JPN",
    "TWN",
    "HKG",
    "MAC",
)
ALLOWED_SCREENSHOT_EXTENSIONS = {".png", ".jpg", ".jpeg"}


class ValidationError(ValueError):
    """Raised when a desired-state source is incomplete or unsafe."""


@dataclass(frozen=True)
class DesiredState:
    manifest: dict[str, Any]
    products: tuple[dict[str, Any], ...]

    @property
    def approval_id(self) -> str:
        release = self.manifest["release"]
        return f"macos-{release['version']}-build-{release['build']}"


def read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise ValidationError(f"Cannot read valid JSON from {path}: {error}") from error


def _require_string(value: Any, field: str, *, minimum: int = 1, maximum: int | None = None) -> str:
    if not isinstance(value, str) or len(value.strip()) < minimum:
        raise ValidationError(f"{field} must contain at least {minimum} characters")
    if maximum is not None and len(value) > maximum:
        raise ValidationError(f"{field} exceeds {maximum} characters")
    return value


def _require_https_url(value: Any, field: str) -> str:
    value = _require_string(value, field)
    parsed = urlparse(value)
    if parsed.scheme != "https" or not parsed.netloc or parsed.username or parsed.password:
        raise ValidationError(f"{field} must be a public HTTPS URL")
    return value


def _validate_app_localization(locale: str, value: Any) -> None:
    if not isinstance(value, dict):
        raise ValidationError(f"app_localizations.{locale} must be an object")
    _require_string(value.get("name"), f"{locale}.name", maximum=30)
    _require_string(value.get("subtitle"), f"{locale}.subtitle", maximum=30)
    _require_string(value.get("description"), f"{locale}.description", maximum=4000)
    keywords = _require_string(value.get("keywords"), f"{locale}.keywords")
    keyword_values = keywords.split(",")
    if any(not keyword.strip() for keyword in keyword_values):
        raise ValidationError(f"{locale}.keywords contains an empty keyword")
    if len(keywords.encode("utf-8")) > 100:
        raise ValidationError(f"{locale}.keywords exceeds 100 UTF-8 bytes")
    if any(len(keyword.strip()) <= 2 for keyword in keyword_values):
        raise ValidationError(f"{locale}.keywords entries must exceed two characters")
    _require_string(value.get("release_notes"), f"{locale}.release_notes", maximum=4000)
    _require_https_url(value.get("support_url"), f"{locale}.support_url")
    _require_https_url(value.get("privacy_policy_url"), f"{locale}.privacy_policy_url")

    screenshots = value.get("screenshots")
    if not isinstance(screenshots, list) or len(screenshots) != 3:
        raise ValidationError(f"{locale}.screenshots must contain the three approved scenes")
    scenes: set[str] = set()
    paths: set[str] = set()
    for index, screenshot in enumerate(screenshots):
        field = f"{locale}.screenshots[{index}]"
        if not isinstance(screenshot, dict):
            raise ValidationError(f"{field} must be an object")
        scene = _require_string(screenshot.get("scene"), f"{field}.scene")
        path = _require_string(screenshot.get("path"), f"{field}.path")
        if Path(path).is_absolute() or ".." in Path(path).parts:
            raise ValidationError(f"{field}.path must stay inside the repository")
        if Path(path).suffix.lower() not in ALLOWED_SCREENSHOT_EXTENSIONS:
            raise ValidationError(f"{field}.path must be a PNG or JPEG")
        scenes.add(scene)
        paths.add(path)
    if len(scenes) != 3 or len(paths) != 3:
        raise ValidationError(f"{locale}.screenshots must use three unique scenes and files")


def validate_manifest(manifest: Any) -> dict[str, Any]:
    if not isinstance(manifest, dict) or manifest.get("schema") != 1:
        raise ValidationError("App Store localization manifest schema must be 1")
    app = manifest.get("app")
    release = manifest.get("release")
    if not isinstance(app, dict):
        raise ValidationError("Manifest requires an app object")
    _require_string(app.get("bundle_id"), "app.bundle_id")
    if app.get("platform") != "MAC_OS":
        raise ValidationError("app.platform must be MAC_OS")
    if app.get("primary_locale") != "ko":
        raise ValidationError("The App Store primary locale must remain ko")
    if release is not None:
        if not isinstance(release, dict):
            raise ValidationError("release must be an object")
        version = _require_string(release.get("version"), "release.version")
        if re.fullmatch(r"\d+\.\d+\.\d+", version) is None:
            raise ValidationError("release.version must use major.minor.patch")
        build = release.get("build")
        if type(build) is not int or build <= 0:
            raise ValidationError("release.build must be a positive integer")

    locales = manifest.get("app_localizations")
    if not isinstance(locales, dict) or set(locales) != set(APP_STORE_LOCALES):
        raise ValidationError(
            "app_localizations must contain exactly " + ", ".join(APP_STORE_LOCALES)
        )
    for locale in APP_STORE_LOCALES:
        _validate_app_localization(locale, locales[locale])

    territories = manifest.get("target_territories")
    if not isinstance(territories, list) or tuple(territories) != TARGET_TERRITORIES:
        raise ValidationError(
            "target_territories must preserve the approved ten-market order"
        )
    if manifest.get("screenshot_display_type") != "APP_DESKTOP":
        raise ValidationError("screenshot_display_type must be APP_DESKTOP")
    return manifest


def _products_list(document: Any) -> list[dict[str, Any]]:
    if not isinstance(document, dict) or document.get("schema") not in {1, 2}:
        raise ValidationError("Commerce localization schema must be 1 or 2")
    if document.get("locales") != list(COMMERCE_LOCALES):
        raise ValidationError("Commerce localization locale order differs")
    products = document.get("products")
    if isinstance(products, list):
        return products
    if isinstance(products, dict):
        return [{"id": product_id, **value} for product_id, value in products.items()]
    raise ValidationError("Commerce localizations require a products list or object")


def validate_products(
    commerce: Any,
    catalog: Any,
) -> tuple[dict[str, Any], ...]:
    if not isinstance(catalog, list):
        raise ValidationError("Commerce catalog must be a list")
    catalog_by_id = {entry.get("id"): entry for entry in catalog if isinstance(entry, dict)}
    products = _products_list(commerce)
    products_by_id: dict[str, dict[str, Any]] = {}
    for product in products:
        if not isinstance(product, dict):
            raise ValidationError("Every localized product must be an object")
        product_id = _require_string(product.get("id"), "products[].id")
        if product_id in products_by_id:
            raise ValidationError(f"Duplicate localized product: {product_id}")
        products_by_id[product_id] = product

    if [product.get("id") for product in products] != list(catalog_by_id):
        raise ValidationError("Localized product order differs from the commerce catalog")
    if set(products_by_id) != set(catalog_by_id):
        missing = sorted(set(catalog_by_id) - set(products_by_id))
        extra = sorted(set(products_by_id) - set(catalog_by_id))
        raise ValidationError(f"Localized product IDs differ; missing={missing}, extra={extra}")

    apple_ids: set[str] = set()
    for product_id in sorted(products_by_id):
        product = products_by_id[product_id]
        catalog_product = catalog_by_id[product_id]
        apple_id = _require_string(
            catalog_product.get("app_store_product_id"),
            f"catalog.{product_id}.app_store_product_id",
        )
        legacy = catalog_product.get("legacy_app_store_product_ids")
        if not isinstance(legacy, list) or not all(isinstance(value, str) for value in legacy):
            raise ValidationError(f"catalog.{product_id}.legacy_app_store_product_ids is invalid")
        for candidate in (apple_id, *legacy):
            if candidate in apple_ids:
                raise ValidationError(f"Duplicate Apple product ID: {candidate}")
            apple_ids.add(candidate)

        localizations = product.get("localizations")
        if not isinstance(localizations, dict) or set(localizations) != set(COMMERCE_LOCALES):
            raise ValidationError(
                f"{product_id}.localizations must contain exactly "
                + ", ".join(COMMERCE_LOCALES)
            )
        for locale in COMMERCE_LOCALES:
            localized = localizations[locale]
            if not isinstance(localized, dict):
                raise ValidationError(f"{product_id}.{locale} must be an object")
            _require_string(
                localized.get("display_name"),
                f"{product_id}.{locale}.display_name",
                minimum=1,
                maximum=30,
            )
            _require_string(
                localized.get("description"),
                f"{product_id}.{locale}.description",
            )
            _require_string(
                localized.get("iap_description"),
                f"{product_id}.{locale}.iap_description",
                maximum=45,
            )
        product["app_store_product_id"] = apple_id
        product["legacy_app_store_product_ids"] = tuple(legacy)
        product["kind"] = catalog_product.get("kind")

    if len(catalog_by_id) != 33 or len(apple_ids) != 43:
        raise ValidationError(
            f"Expected 33 current products and 43 StoreKit IDs, got "
            f"{len(catalog_by_id)} and {len(apple_ids)}"
        )
    return tuple(products_by_id[product_id] for product_id in sorted(products_by_id))


def load_desired_state(manifest_path: Path, commerce_path: Path, catalog_path: Path) -> DesiredState:
    manifest_source = read_json(manifest_path)
    if isinstance(manifest_source, dict) and "release" not in manifest_source:
        version_path = manifest_path.with_name("version.json")
        version_source = read_json(version_path)
        if not isinstance(version_source, dict):
            raise ValidationError(f"Version source must be an object: {version_path}")
        manifest_source = dict(manifest_source)
        manifest_source["release"] = {
            "version": f"{version_source.get('head')}.{version_source.get('yearweek')}.{version_source.get('macBuild')}",
            "build": version_source.get("macBuild"),
        }
    manifest = validate_manifest(manifest_source)
    release_path = manifest_path.with_name("macos.json")
    if release_path.is_file():
        release = read_json(release_path)
        expected = manifest["release"]
        if (
            release.get("schema") != 1
            or release.get("platform") != "macos"
            or release.get("channel") != "appstore"
            or release.get("version") != expected["version"]
            or release.get("build") != expected["build"]
        ):
            raise ValidationError(
                "App Store localization release differs from release/macos.json"
            )
    try:
        commerce_source = read_commerce_source(commerce_path)
    except (OSError, ValueError) as error:
        raise ValidationError(
            f"Cannot load commerce localizations from {commerce_path}: {error}"
        ) from error
    products = validate_products(commerce_source, read_json(catalog_path))
    return DesiredState(manifest=manifest, products=products)


def expanded_iap_localizations(product: dict[str, Any]) -> dict[str, dict[str, str]]:
    result: dict[str, dict[str, str]] = {}
    source = product["localizations"]
    for locale in APP_STORE_LOCALES:
        source_locale = "en" if locale in ENGLISH_APP_STORE_LOCALES else locale
        value = source[source_locale]
        result[locale] = {
            "name": app_store_display_name(
                product["id"],
                source_locale,
                value["display_name"],
                product["kind"],
            ),
            "description": value["iap_description"],
        }
    return result
