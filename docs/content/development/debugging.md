# Debugging

Enable debug logging for the SmartLists plugin to troubleshoot issues or get detailed information about playlist operations.

## Enable Debug Logging

To enable debug logging specifically for the SmartLists plugin:

1. **Create a logging configuration file** directly in your Jellyfin config directory:
   ```
   {JellyfinConfigPath}/logging.json
   ```
   
   Do not add another `config` path segment if your Jellyfin config directory already ends in `config`.

   Typical full paths for `logging.json` are:
   - **Docker**: `/config/config/logging.json` inside the container, or `{host path mounted as /config}/config/logging.json` on the host
   - **Linux system install**: `/var/lib/jellyfin/config/logging.json`
   - **Windows**: `C:\ProgramData\Jellyfin\Server\config\logging.json`
   - **macOS**: `~/Library/Application Support/Jellyfin/Server/config/logging.json`

   For this repository's local development Docker setup, the host path is:
   ```
   dev/jellyfin-data/config/config/logging.json
   ```

2. **Add the following content** to `logging.json`:
   ```json
   {
     "Serilog": {
       "MinimumLevel": {
         "Override": {
           "Jellyfin.Plugin.SmartLists": "Debug"
         }
       }
     }
   }
   ```

3. **Restart Jellyfin** for the changes to take effect

## Viewing Logs

After enabling debug logging, you can view the detailed logs:

- **Log location**: `{JellyfinDataPath}/log/` (for Docker, usually `/config/log/` inside the container)
- **Log files**: Look for files named `log_YYYYMMDD.log` (e.g., `log_20251109.log`)

The debug logs will include detailed information about:
- Playlist refresh operations
- Rule evaluation
- Item filtering and matching
- Performance metrics
- Error details and stack traces

## Sharing Logs for Troubleshooting

When seeking help with issues, you may need to share log files. The easiest way to access logs is through the Jellyfin admin dashboard:

1. **Access Logs via Admin Dashboard**:
    - Go to **Dashboard** → **Logs**
    - Select the log file you want to view (e.g., `log_20251109.log`)
    - Copy the relevant log entries or download the whole log

2. **Upload to Pastebin**:
    - Go to [bin.dinsten.se](https://bin.dinsten.se/)
    - Paste the log content (or attach the entire log file if needed)
    - Share the paste URL when reporting issues

3. **For very large logs**:
    - Focus on the time period when the issue occurred
    - Copy only the relevant sections containing SmartLists entries

!!! tip "Privacy Note"
    Logs may contain sensitive information. Review logs before sharing and consider redacting any personal information if needed.

## Disable Debug Logging

To disable debug logging:

1. **Delete or rename** the `logging.json` file
2. **Restart Jellyfin**

Or modify the file to remove the SmartLists override section.

!!! tip "Performance Note"
    Debug logging generates significantly more log output and may impact performance. Only enable it when troubleshooting issues or during development.
