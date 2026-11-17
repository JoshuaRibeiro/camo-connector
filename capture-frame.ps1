function Open-CaptureFrame([string]$Path) {
    $stream = $null
    $image = $null
    try {
        if ([IO.Directory]::Exists($Path)) {
            $Path = [IO.Directory]::GetFiles($Path, 'frame-*.jpg') | Sort-Object -Descending | Select-Object -First 1
            if (!$Path) { return }
        }
        if ([DateTime]::UtcNow - [IO.File]::GetLastWriteTimeUtc($Path) -gt [TimeSpan]::FromSeconds(3)) { return }
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
            [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        $image = [Drawing.Image]::FromStream($stream)
        return [Drawing.Bitmap]::new($image)
    } catch [IO.IOException] {
        # The publisher can replace the frame between the timestamp check and open.
        return
    } catch [ArgumentException] {
        return
    } finally {
        if ($image) { $image.Dispose() }
        if ($stream) { $stream.Dispose() }
    }
}
