# App Store 현지화 동기화

App Store Connect 현지화는
[`release/app-store-localizations.json`](../../release/app-store-localizations.json)과
언어별 [`assets/v1/locale/commerce/`](../../assets/v1/locale/commerce/) source를 기준으로
비교한다. 이 절차는 앱 업로드, 심사 제출,
출시를 수행하지 않는다. 원격 metadata 변경도 별도 release 승인이 없으면 실행하지 않는다.

## 자격 증명

CI는 다음 값을 secret 환경 변수로 주입한다.

- `APP_STORE_CONNECT_ISSUER_ID`
- `APP_STORE_CONNECT_KEY_ID`
- `APP_STORE_CONNECT_PRIVATE_KEY`: `.p8` 파일의 PEM 내용을 그대로 저장한 secret

로컬에서는 issuer ID와 key ID만 환경 변수로 지정하고, private key는 macOS Keychain의
generic password item에서 읽는다. Service는 `SIDEY.AppStoreConnect.APIKey`, Account는
key ID다. Keychain Access에서 이 항목을 만든다. Private key 파일 경로를 받는 환경 변수나
CLI 옵션은 지원하지 않으며, repository·작업 디렉터리·CI artifact에 key 파일을 두지 않는다.

## Snapshot과 기본 diff

다음 명령은 App Store Connect를 읽기만 하고 snapshot과 diff plan을 로컬에 기록한다.

```sh
python3 scripts/app_store_connect/sync.py \
  --snapshot-output /private/tmp/sidey-asc-snapshot.json \
  --plan-output /private/tmp/sidey-asc-plan.json
```

이미 고정한 snapshot을 다시 비교할 때는 자격 증명 없이 실행할 수 있다. 이 경로는 항상
dry-run이며 apply에 사용할 수 없다.

```sh
python3 scripts/app_store_connect/sync.py \
  --snapshot /private/tmp/sidey-asc-snapshot.json \
  --plan-output /private/tmp/sidey-asc-plan-recheck.json
```

Plan의 `blockers`가 비어 있어야 한다. 실제 현지화 build에서 만든 언어별 screenshot 파일이
manifest 경로에 없거나, 현재 판매 상품이 Connect snapshot에서 누락되거나, availability
snapshot이 불완전하면 apply를 거부한다. Legacy Apple product ID는 복원 매핑만 검증하고
현지화나 판매 지역 변경 대상으로 만들지 않는다.

Repository의 `release/version.json`에 있는 macOS build는 App Store Connect snapshot을 읽기 전까지
provisional이다. Snapshot은 macOS pre-release version의 전체 build upload 이력과 현재
최댓값을 기록한다. Manifest build가 이미 사용됐거나 정확히 `max + 1`이 아니면 blocker다.
대상 App Store version이 더 이상 수정 가능한 상태가 아니면 같은 version을 억지로
재사용하지 않고 다음 minor version이 필요한지 version audit를 다시 수행한다.

## 승인된 apply

원격 변경은 다음 세 조건을 모두 만족해야 한다.

1. Release 담당자가 정확한 version/build의 원격 변경을 별도로 승인한다.
2. 승인 ID를 `SIDEY_APP_STORE_RELEASE_APPROVAL` 환경 gate로 주입한다.
3. 같은 ID를 `--release-approval`과 함께 전달하고 `--apply`를 명시한다.

현재 승인 ID는 공통 version source의 Product Version과 macOS build에서
`macos-<version>-build-<build>` 형식으로 파생한다.

```sh
product_version=$(python3 scripts/sidey_version.py --get macVersion)
mac_build=$(python3 scripts/sidey_version.py --get macBuild)
approval_id="macos-${product_version}-build-${mac_build}"
SIDEY_APP_STORE_RELEASE_APPROVAL="$approval_id" \
python3 scripts/app_store_connect/sync.py \
  --apply \
  --release-approval "$approval_id" \
  --snapshot-output /private/tmp/sidey-asc-approved-snapshot.json \
  --plan-output /private/tmp/sidey-asc-approved-plan.json
```

Apply는 반드시 App Store Connect에서 새 snapshot을 읽고 그 상태로 diff를 다시 계산한다.
고정 snapshot과 `--apply`를 함께 쓸 수 없다. 앱 availability는 목표 시장 중 비활성 상태만
활성화한다. IAP availability API에는 기존 시장과 목표 시장의 합집합을 보내므로 기존
storefront를 제거하지 않으며, `availableInNewTerritories`의 기존 값도 보존한다.

원격 변경 후에는 새 snapshot으로 diff를 다시 실행해 operation과 blocker가 0인지 확인한다.
심사 제출, 수동 출시, 중국 본토 감사, EU/DSA 확장은 각각 별도 승인과 절차가 필요하다.
