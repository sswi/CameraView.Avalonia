import { dotnet } from './_framework/dotnet.js'

// ==============================
// Camera support for C# JSImport
// ==============================

const _state = {
    video: null, canvas: null, ctx: null,
    width: 640, height: 480,
};

let _cameras = [];  // [{ deviceId, label }]

export async function startCamera(deviceId, width, height) {
    stopCamera();
    _state.width = width || 640;
    _state.height = height || 480;

    // 先刷新设备列表
    await _refreshCameras();

    // 用 deviceId 或空（自动选）
    const constraints = { video: true, audio: false };
    if (deviceId) {
        constraints.video = { deviceId: { exact: deviceId } };
    }
    try {
        const stream = await navigator.mediaDevices.getUserMedia(constraints);
        _state.video = document.createElement('video');
        _state.video.playsInline = true;
        _state.video.muted = true;
        _state.video.srcObject = stream;
        await _state.video.play();
        console.log('CameraView: started', deviceId || '(auto)');
        return true;
    } catch (e) {
        console.warn('CameraView: startCamera failed', deviceId, e.message);
        // 如果 exact 失败，尝试不带约束
        if (deviceId) {
            try {
                const fallback = await navigator.mediaDevices.getUserMedia({ video: true, audio: false });
                _state.video = document.createElement('video');
                _state.video.playsInline = true;
                _state.video.muted = true;
                _state.video.srcObject = fallback;
                await _state.video.play();
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
    _state.video = null; _state.canvas = null; _state.ctx = null;
}

export function getFrameData() {
    if (!_state.ctx || !_state.video) return null;
    _state.ctx.drawImage(_state.video, 0, 0, _state.width, _state.height);
    return new Uint8Array(_state.ctx.getImageData(0, 0, _state.width, _state.height).data.buffer);
}

export function capturePhoto() {
    if (!_state.ctx || !_state.video) return '';
    _state.ctx.drawImage(_state.video, 0, 0, _state.width, _state.height);
    return _state.canvas.toDataURL('image/jpeg', 0.92);
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
