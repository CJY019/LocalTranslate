$ErrorActionPreference = 'Stop'
$utf8 = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8

function Await-WinRT($operation, [Type] $resultType) {
    $task = $asTask.MakeGenericMethod($resultType).Invoke($null, @($operation))
    $task.GetAwaiter().GetResult()
}

function Find-Language($available, $patterns) {
    foreach ($pattern in $patterns) {
        foreach ($language in $available) {
            if ($language.LanguageTag -like $pattern) { return $language }
        }
    }
}

function Resolve-Language($available, $requested, $english, $chinese) {
    $exact = $available | Where-Object { $_.LanguageTag -eq $requested } | Select-Object -First 1
    if ($exact) { return $exact }
    switch ($requested) {
        'en' { return $english }
        'zh' { return $chinese }
        'zh-Hans' { return Find-Language $available @('zh-Hans-CN', 'zh-Hans*', 'zh-CN', 'zh-SG') }
        'zh-Hant' { return Find-Language $available @('zh-Hant-HK', 'zh-Hant*', 'zh-HK', 'zh-TW', 'zh-MO') }
    }
    if ($requested -match '^zh-(CN|SG)$') {
        return Find-Language $available @('zh-Hans-' + $Matches[1])
    }
    if ($requested -match '^zh-(HK|MO|TW)$') {
        return Find-Language $available @('zh-Hant-' + $Matches[1])
    }
    if ($requested -match '^zh-Hans-(CN|SG)$' -or $requested -match '^zh-Hant-(HK|MO|TW)$') {
        return Find-Language $available @('zh-' + $Matches[1])
    }
    if ($requested -match '^[a-z]{2,3}$') {
        return Find-Language $available @($requested + '-*')
    }
}

function Read-ImageText($language, $bitmap) {
    $engine = $ocrType::TryCreateFromLanguage($language)
    if ($null -eq $engine) { return }
    [pscustomobject]@{
        language_tag = $engine.RecognizerLanguage.LanguageTag
        result = Await-WinRT ($engine.RecognizeAsync($bitmap)) $resultType
    }
}

function Read-Automatically($available, $english, $chinese, $bitmap) {
    if ($english -and $chinese) {
        $en = Read-ImageText $english $bitmap
        $zh = Read-ImageText $chinese $bitmap
        if ($null -eq $zh) { return $en }
        # WinRT provides no confidence score or language detection. Use the
        # Chinese engine when its result contains repeated evidence of Han text.
        $hanCount = [regex]::Matches([string] $zh.result.Text, '[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF]').Count
        if ($hanCount -ge 2 -or [string]::IsNullOrWhiteSpace($en.result.Text)) { return $zh }
        return $en
    }
    if ($chinese) { return Read-ImageText $chinese $bitmap }
    if ($english) { return Read-ImageText $english $bitmap }
    $profileEngine = $ocrType::TryCreateFromUserProfileLanguages()
    if ($profileEngine) { return Read-ImageText $profileEngine.RecognizerLanguage $bitmap }
    return Read-ImageText $available[0] $bitmap
}

function Get-Lines($result, $width, $height, $bitmap) {
    $xScale = $width / $bitmap.PixelWidth
    $yScale = $height / $bitmap.PixelHeight
    foreach ($line in $result.Lines) {
        if ([string]::IsNullOrWhiteSpace($line.Text)) { continue }
        $left = [double] $bitmap.PixelWidth
        $top = [double] $bitmap.PixelHeight
        $right = 0.0
        $bottom = 0.0
        foreach ($word in $line.Words) {
            $box = $word.BoundingRect
            $left = [math]::Min($left, $box.X)
            $top = [math]::Min($top, $box.Y)
            $right = [math]::Max($right, $box.X + $box.Width)
            $bottom = [math]::Max($bottom, $box.Y + $box.Height)
        }
        $x = [int] [math]::Floor([math]::Max(0.0, $left * $xScale))
        $y = [int] [math]::Floor([math]::Max(0.0, $top * $yScale))
        $right = [int] [math]::Ceiling([math]::Min([double] $width, $right * $xScale))
        $bottom = [int] [math]::Ceiling([math]::Min([double] $height, $bottom * $yScale))
        [pscustomobject]@{
            text = $line.Text
            x = $x
            y = $y
            width = [math]::Max(0, $right - $x)
            height = [math]::Max(0, $bottom - $y)
        }
    }
}

function Test-UniformColor([byte[]] $pixels) {
    $reference = [byte[]]::new($pixels.Length)
    [Array]::Copy($pixels, $reference, 4)
    for ($filled = 4; $filled -lt $reference.Length; $filled *= 2) {
        [Array]::Copy($reference, 0, $reference, $filled, [Math]::Min($filled, $reference.Length - $filled))
    }
    $hash = [System.Security.Cryptography.SHA256]::Create()
    try {
        $referenceHash = [Convert]::ToBase64String($hash.ComputeHash($reference))
        return $referenceHash -ceq [Convert]::ToBase64String($hash.ComputeHash($pixels))
    } finally {
        $hash.Dispose()
    }
}

function Get-VisualImageBase64($bitmap) {
    Add-Type -AssemblyName WindowsBase, PresentationCore
    $width = $bitmap.PixelWidth
    $height = $bitmap.PixelHeight
    $stride = $width * 4
    $pixels = [byte[]]::new($stride * $height)
    $buffer = [System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions]::AsBuffer($pixels)
    $bitmap.CopyToBuffer($buffer)
    if (Test-UniformColor $pixels) { return }
    $format = [System.Windows.Media.PixelFormats]::Pbgra32
    $source = [System.Windows.Media.Imaging.BitmapSource]::Create($width, $height, 96, 96, $format, $null, $pixels, $stride)
    $bounds = [System.Windows.Rect]::new(0, 0, $width, $height)
    $rendered = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($width, $height, 96, 96, $format)
    foreach ($background in @([System.Windows.Media.Brushes]::White, [System.Windows.Media.Brushes]::Black)) {
        $visual = [System.Windows.Media.DrawingVisual]::new()
        $context = $visual.RenderOpen()
        try {
            $context.DrawRectangle($background, $null, $bounds)
            $context.DrawImage($source, $bounds)
        } finally {
            $context.Close()
        }
        $rendered.Render($visual)
        $rendered.CopyPixels($pixels, $stride, 0)
        if (-not (Test-UniformColor $pixels)) { break }
    }
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rendered))
    $output = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($output)
        [Convert]::ToBase64String($output.ToArray())
    } finally {
        $output.Dispose()
    }
}

$stage = 'unavailable'
$stream = $null
$randomStream = $null
$bitmap = $null
try {
    $request = ConvertFrom-Json -InputObject ([Console]::In.ReadToEnd())
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $ocrType = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
    $decoderType = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType=WindowsRuntime]
    $available = @($ocrType::AvailableRecognizerLanguages)
    $maxDimension = $ocrType::MaxImageDimension
    if ($request.action -eq 'capabilities') {
        $decoders = $decoderType::GetDecoderInformationEnumerator()
        $payload = @{
            languages = @(foreach ($language in $available) {
                @{ tag = $language.LanguageTag; name = $language.DisplayName }
            })
            webp = [bool] ($decoders | Where-Object { $_.FriendlyName -like '*WebP*' })
            max_image_dimension = $maxDimension
        }
        [Console]::WriteLine((ConvertTo-Json -InputObject $payload -Depth 5 -Compress))
        exit
    }

    $prepareVisual = $request.action -eq 'prepare_visual'
    if (-not $prepareVisual) {
        $stage = 'no_languages'
        if ($available.Count -eq 0) { throw 'OCR language list is empty.' }
        $stage = 'language_unavailable'
        $english = Find-Language $available @('en-US', 'en', 'en-*')
        $chinese = Find-Language $available @('zh-Hans-CN', 'zh-Hans*', 'zh-CN', 'zh-SG', 'zh', 'zh-Hant*', 'zh-*')
        $requested = [string] $request.language
        if ($requested -ne 'auto') {
            $language = Resolve-Language $available $requested $english $chinese
            if ($null -eq $language) { throw 'OCR language is not installed.' }
        }
    }

    $stage = 'decode_failed'
    $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and
        $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } | Select-Object -First 1
    $stream = [System.IO.File]::OpenRead($request.path)
    $randomStream = [System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($stream)
    $decoder = Await-WinRT ($decoderType::CreateAsync($randomStream)) $decoderType
    $width = [int] $decoder.OrientedPixelWidth
    $height = [int] $decoder.OrientedPixelHeight

    $transform = [Windows.Graphics.Imaging.BitmapTransform, Windows.Graphics.Imaging, ContentType=WindowsRuntime]::new()
    # A scaled bitmap keeps memory bounded for large scans and screenshots.
    $maxArea = 32000000.0
    $alphaMode = 'Ignore'
    if ($prepareVisual) {
        $maxDimension = 1792
        $maxArea = 2000000.0
        $alphaMode = 'Premultiplied'
    }
    $dimensionScale = $maxDimension / [math]::Max($width, $height)
    $areaScale = [math]::Sqrt($maxArea / ($width * 1.0 * $height))
    $scale = [math]::Min(1.0, [math]::Min($dimensionScale, $areaScale))
    if ($scale -lt 1) {
        $transform.ScaledWidth = [uint32] [math]::Max(1, [math]::Floor($decoder.PixelWidth * $scale))
        $transform.ScaledHeight = [uint32] [math]::Max(1, [math]::Floor($decoder.PixelHeight * $scale))
        $transform.InterpolationMode = 'Fant'
    }
    $bitmapType = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics.Imaging, ContentType=WindowsRuntime]
    $bitmap = Await-WinRT ($decoder.GetSoftwareBitmapAsync(
        'Bgra8', $alphaMode, $transform, 'RespectExifOrientation', 'DoNotColorManage')) $bitmapType

    if ($prepareVisual) {
        $encoded = Get-VisualImageBase64 $bitmap
        if (-not $encoded) {
            $stage = 'no_text'
            throw 'The image is a uniform color.'
        }
        $payload = @{width = $width; height = $height; base64 = $encoded}
        [Console]::WriteLine((ConvertTo-Json -InputObject $payload -Compress))
        exit
    }

    $stage = 'recognition_failed'
    $resultType = [Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType=WindowsRuntime]
    $recognized = if ($requested -eq 'auto') {
        Read-Automatically $available $english $chinese $bitmap
    } else {
        Read-ImageText $language $bitmap
    }
    if ($null -eq $recognized) { throw 'OCR engine could not be created.' }

    $stage = 'no_text'
    $lines = @(Get-Lines $recognized.result $width $height $bitmap)
    if ($lines.Count -eq 0) { throw 'OCR produced no text.' }
    $payload = @{
        language_tag = $recognized.language_tag
        width = $width
        height = $height
        lines = $lines
        text = ($lines | ForEach-Object { $_.text }) -join "`n"
    }
    [Console]::WriteLine((ConvertTo-Json -InputObject $payload -Depth 5 -Compress))
} catch {
    [Console]::WriteLine((ConvertTo-Json -InputObject @{error = $stage} -Compress))
} finally {
    if ($bitmap) { $bitmap.Dispose() }
    if ($randomStream) { $randomStream.Dispose() }
    if ($stream) { $stream.Dispose() }
}
