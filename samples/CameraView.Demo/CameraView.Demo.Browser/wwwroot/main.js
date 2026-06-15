import { dotnet } from './_framework/dotnet.js'

// ==============================
// Camera support for C# JSImport
// ==============================

const _state = {
    video: null, canvas: null, ctx: null,
    width: 640, height: 480,
    deviceId: '',
};

let _cameras = [];  // [{ deviceId, label }]

const _commonResolutions = [
    { width: 4032, height: 3024 },
    { width: 3840, height: 2160 },
    { width: 3264, height: 2448 },
    { width: 2560, height: 1440 },
    { width: 1920, height: 1080 },
    { width: 1600, height: 1200 },
    { width: 1280, height: 960 },
    { width: 1280, height: 720 },
    { width: 1024, height: 768 },
    { width: 800, height: 600 },
    { width: 640, height: 480 },
];

export async function startCamera(deviceId, width, height) {
    stopCamera();
    _state.width = width || 640;
    _state.height = height || 480;
    _state.deviceId = deviceId || '';

    // 先刷新设备列表
    await _refreshCameras();

    // 用 deviceId 或空（自动选）
    const constraints = {
        video: {
            width: { ideal: _state.width },
            height: { ideal: _state.height },
        },
        audio: false,
    };
    if (deviceId) {
        constraints.video.deviceId = { exact: deviceId };
    }
    try {
        const stream = await navigator.mediaDevices.getUserMedia(constraints);
        _state.video = document.createElement('video');
        _state.video.playsInline = true;
        _state.video.muted = true;
        _state.video.srcObject = stream;
        await _state.video.play();
        await new Promise(r => setTimeout(r, 200));
        _syncStateFromTrack(stream);
        // 创建画布 — 放在 startCamera 方法结束前，与异步无关
        _state.canvas = document.createElement('canvas');
        _state.canvas.width = _state.width;
        _state.canvas.height = _state.height;
        _state.ctx = _state.canvas.getContext('2d', { willReadFrequently: true });
        console.log('CameraView: ctx created', !!_state.ctx, !!_state.canvas);
        console.log('CameraView: started', deviceId || '(auto)');
        return true;
    } catch (e) {
        console.warn('CameraView: startCamera failed', deviceId, e.message);
        if (deviceId) {
            try {
                const fallback = await navigator.mediaDevices.getUserMedia({ video: true, audio: false });
                _state.video = document.createElement('video');
                _state.video.playsInline = true;
                _state.video.muted = true;
                _state.video.srcObject = fallback;
                await _state.video.play();
                await new Promise(r => setTimeout(r, 200));
                _syncStateFromTrack(fallback);
                if (!_state.canvas) {
                    _state.canvas = document.createElement('canvas');
                    _state.canvas.width = _state.width;
                    _state.canvas.height = _state.height;
                }
                if (!_state.ctx) {
                    _state.ctx = _state.canvas.getContext('2d', { willReadFrequently: true });
                    console.log('CameraView: ctx created (fallback)', !!_state.ctx);
                }
                console.log('CameraView: started (fallback)');
                return true;
            } catch (e2) {
                console.warn('CameraView: fallback also failed', e2.message);
                return false;
            }
        }
        return false;
    }
}

export function stopCamera() {
    if (_state.video) {
        const tracks = _state.video.srcObject?.getTracks();
        if (tracks) tracks.forEach(t => t.stop());
        _state.video.srcObject = null;
    }
    _state.deviceId = '';
    _state.video = null; _state.canvas = null; _state.ctx = null;
}

export function getFrameData() {
    if (!_state.ctx || !_state.video) {
        console.log('CameraView: getFrameData skipped', {ctx: !!_state.ctx, video: !!_state.video});
        return null;
    }
    _state.ctx.drawImage(_state.video, 0, 0, _state.width, _state.height);
    const imageData = _state.ctx.getImageData(0, 0, _state.width, _state.height);
    console.log('CameraView: frame', imageData.data.length, 'bytes');
    return new Uint8Array(imageData.data.buffer);
}

export function capturePhoto() {
    if (!_state.ctx || !_state.video) return '';
    _state.ctx.drawImage(_state.video, 0, 0, _state.width, _state.height);
    return _state.canvas.toDataURL('image/jpeg', 0.92);
}

export function getCameraInfo() {
    return JSON.stringify({
        deviceId: _state.deviceId || '',
        width: _state.width,
        height: _state.height,
    });
}

export function getSupportedResolutions() {
    const track = _state.video?.srcObject?.getVideoTracks?.()[0];
    if (!track) return '[]';

    const settings = track.getSettings?.() || {};
    const capabilities = track.getCapabilities?.() || {};

    const widthMin = capabilities.width?.min ?? settings.width ?? _state.width;
    const widthMax = capabilities.width?.max ?? settings.width ?? _state.width;
    const heightMin = capabilities.height?.min ?? settings.height ?? _state.height;
    const heightMax = capabilities.height?.max ?? settings.height ?? _state.height;

    const resolutions = _commonResolutions
        .filter(r => r.width >= widthMin && r.width <= widthMax && r.height >= heightMin && r.height <= heightMax)
        .map(r => ({
            width: r.width,
            height: r.height,
            label: _createResolutionLabel(r.width, r.height),
        }));

    if (settings.width && settings.height) {
        const exists = resolutions.some(r => r.width === settings.width && r.height === settings.height);
        if (!exists) {
            resolutions.push({
                width: settings.width,
                height: settings.height,
                label: _createResolutionLabel(settings.width, settings.height),
            });
        }
    }

    resolutions.sort((a, b) => (b.width * b.height) - (a.width * a.height) || b.width - a.width);
    return JSON.stringify(resolutions);
}

async function _refreshCameras() {
    try {
        const devices = await navigator.mediaDevices.enumerateDevices();
        _cameras = devices.filter(d => d.kind === 'videoinput').map(d => ({
            deviceId: d.deviceId,
            label: d.label || `Camera #${d.deviceId.slice(0, 8)}`,
        }));
        console.log('CameraView: refresh', _cameras.length, 'cameras');
    } catch { _cameras = []; }
}

/** 返回 JSON: [{ deviceId, label }] */
export async function enumerateCameras() {
    await _refreshCameras();
    return JSON.stringify(_cameras);
}

function _syncStateFromTrack(stream) {
    const track = stream?.getVideoTracks?.()[0];
    if (!track) return;

    const settings = track.getSettings?.() || {};
    _state.deviceId = settings.deviceId || _state.deviceId || '';
    _state.width = settings.width || _state.video?.videoWidth || _state.width;
    _state.height = settings.height || _state.video?.videoHeight || _state.height;
}

function _createResolutionLabel(width, height) {
    const ratio = width / height;
    const megapixels = (width * height) / 1000000;
    const ratioLabel = Math.abs(ratio - 4 / 3) < 0.03
        ? '4:3'
        : Math.abs(ratio - 16 / 9) < 0.03
            ? '16:9'
            : Math.abs(ratio - 1) < 0.03
                ? '1:1'
                : `${ratio.toFixed(2)}:1`;

    return `${ratioLabel} ${megapixels.toFixed(1)}MP`;
}

// ==============================
// .NET runtime startup
// ==============================

const is_browser = typeof window != "undefined";
if (!is_browser) throw new Error(`Expected to be running in a browser`);

const dotnetRuntime = await dotnet
    .withDiagnosticTracing(false)
    .withApplicationArgumentsFromQuery()
    .create();

const config = dotnetRuntime.getConfig();

await dotnetRuntime.runMain(config.mainAssemblyName, [globalThis.location.href]);
