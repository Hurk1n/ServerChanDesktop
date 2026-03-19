# ServerChan3 APK Reverse Notes

Source APK:

- `D:\xwechat_files\wxid_u7to2n7401lw22_90a4\msg\file\2026-03\serverchan3-1.1.0-b94.apk`

What was confirmed:

- The APK is a Flutter AOT application.
- Core app logic is compiled into `lib/arm64-v8a/libapp.so`.
- Java/Kotlin mainly wraps vendor push SDKs and is not where the Server3 inbox flow lives.

Recovered clues from `libapp.so`:

- Flutter pages and routes:
  - `/login`
  - `/tagSettings`
  - `/sc3/device/save`
  - `/sc3/push/index`
- Server3 bot/inbox routes:
  - `/sc3/bot/index`
  - `/sc3/bot/messages?bot_id=`
  - `/sc3/bot/message/remove`
  - `/sc3/bot/messages/clean`
  - `/sc3/bot/toggle`
- Login and account strings:
  - `https://bot.ftqq.com/login/by/sendkey`
  - `auth_token`
  - `bot_token`
  - `sendkey`
  - `PushNotificationEvent`
  - `_NotificationPageState`
- Local state clues exposed in the binary:
  - `is_unread`
  - `is_starred`
  - `getUnreadNotificationCount`
  - `markUnstarredNotificationsAsDeleted`

Live endpoint verification done against the service:

- `POST https://bot.ftqq.com/login/by/sendkey`
  - body: `sendkey=<main sendkey>`
  - returns JSON with a root-level `token`
- `GET https://bot.ftqq.com/sc3/push/index`
  - header: `Authorization: Bearer <token>`
  - returns JSON with `pushes.meta` and `pushes.data`
  - `pushes.data` entries include fields such as:
    - `id`
    - `title`
    - `desp`
    - `created_at`
    - `updated_at`
- `GET https://bot.ftqq.com/sc3/bot/index`
  - same Bearer token auth
  - returns `bots`

Important correction:

- The previous desktop rebuild incorrectly reused the old `sctapi.ftqq.com/app/key2token -> /app/user/mypush` inbox path.
- The new APK and live validation show that the practical Server3 receive flow is:
  - `SendKey -> POST /login/by/sendkey -> Bearer token -> GET /sc3/push/index`
- The mobile app's unread/starred behavior appears to be maintained locally, not fully supplied by the server response.

Windows rebuild strategy:

- Keep `sctapi.ftqq.com/{sendkey}.send` only for sending.
- Use `bot.ftqq.com` for inbox login and message retrieval.
- Persist local read/star state on Windows to mirror the APK behavior closely enough.
- Keep web entrypoints only as helper navigation, not as the inbox implementation.
