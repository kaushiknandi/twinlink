# TwinLink Downloader 2.3

TwinLink downloads a direct HTTP or HTTPS file on Windows. You can start on home Wi-Fi and plug in USB tethering during the download. The window shows how much of the file each connection supplied, live speeds in Mbps, the combined speed, elapsed time and estimated time remaining, and remaining size based on verified download progress.

## Use the app

1. Close the older TwinLink window. Extract the ZIP into a new folder and double-click **TwinLink.exe**. Windows 10 or 11 with .NET Framework 4.8 is required.
2. Select Wi-Fi as the first connection. Leave **Automatically add USB tethering** selected, or choose a specific second adapter such as **Ethernet 3**. To measure a single connection, choose **Use first connection only**.
3. Paste the host's direct file download URL and click **Download**. Once the server responds, choose a new filename. TwinLink uses the server's suggested filename when available. It preserves existing files.
4. To add cellular bandwidth, enable mobile data and USB tethering on the phone. Turn off the phone's Wi-Fi so it uses cellular data. During a ranged download the second connection can join while there are still unassigned parts. Cellular traffic counts toward the phone's data allowance.
5. The mode line says **Downloading over both connections** after each has supplied verified data. When a single stream is necessary it shows the reason and the active address. For streams of unknown size, the bytes, speed and elapsed time remain visible.

A direct MP4, MKV, PDF, ZIP or installer is ordinary file data. To get a media file, use the site's download button or authorized file URL. A video watch page can return HTML, and an HLS or DASH playlist contains instructions for a player. If you actually want the page or playlist, choose its own filename extension.

USB is optional. If the connection closes before the response headers arrive, TwinLink can retry over the selected adapter using the Windows curl.exe client. You can leave automatic USB detection enabled during a Wi-Fi-only download.

## If a download fails

| What TwinLink shows | What it means |
| --- | --- |
| Single connection | The server accepts a regular download. TwinLink can finish it on one connection when the server ignores ranges, declines extra requests or supplies insufficient file-version information. |
| HTTP 401 or 403 on the ordinary download | The host requires sign-in or denies this request. Copy a fresh authorized link from the site. Use its signed-in browser download if access is tied to that session. |
| Connection timed out | TwinLink tries the other selected, available connection after a connection failure. If both fail, check the URL and each network's internet access. |
| HTTP 404, 410 or 429 | The URL is missing, expired or rate-limited. Obtain a current link or wait for the period indicated by the host. |

After a run, click **Copy details** to copy the host name, adapter information, mode changes, speeds and result. The report omits the download URL's path and query string. TwinLink uses only the cookies the host sets for this download and does not import a browser session.

## How the transfer works

TwinLink opens an ordinary GET on the selected connection and keeps that response available while it tests a byte range. A compatible server returns HTTP 206 with the requested offsets, a total size and a usable version identifier (a strong ETag or Last-Modified date). Up to four workers per adapter then claim ranges from a shared queue and write them at their assigned file offsets. The Windows HTTP stack handles redirects, connection reuse, TLS, compression and chunked response framing. Each adapter has its own connection pool with sockets bound to its selected IPv4 address.

Each worker adjusts its next range size using the speed of its completed requests. Near the end, workers claim smaller ranges so a slower adapter has less outstanding work. Run tests/run.cmd --benchmark to try the local two-speed simulation.

Each range is checked before it is credited. An interrupted range goes back into the queue. If a server changes the file or starts returning inconsistent ranges, TwinLink discards the partial result before attempting one fresh ordinary download. If an ordinary stream breaks, it can restart from byte zero. The final filename appears only after the transfer succeeds. Cancellation and failed downloads remove TwinLink's temporary output during normal operation.

Performance depends on the two routes and the host's capacity. To compare speeds, use the same large file for Wi-Fi only, USB only and both connections. Repeat the runs and compare elapsed times.

## Build, test and share

Double-click **build.cmd** to compile the C# source with the Windows .NET Framework compiler. **Start TwinLink.cmd** opens the compiled application and builds it if the executable is missing.

Run **tests/run.cmd** to exercise the downloader against a local HTTP test server. The tests use two loopback source addresses, verify the completed bytes and cover late attachment, refused range requests, timeouts, redirects, interrupted responses, unknown file sizes and cancellation. They do not use mobile data.

For GitHub, commit the source, tests, README and build scripts. Attach the ZIP or compiled TwinLink.exe to a release so others can run it without building it.
