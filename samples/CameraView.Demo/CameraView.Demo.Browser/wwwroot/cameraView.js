/**
 * CameraView Browser Helper — WebRTC getUserMedia + Canvas 2D
 *
 * 配合 BrowserCameraProvider.cs ([JSImport] from "cameraView.js") 使用。
 * 将此文件放入 WASM 项目的 wwwroot/ 目录即可。
 *
 * 导出的函数会被 .NET WASM 运行时动态加载。
 */

const _state = {
    stream: null,
    video: null,
    canvas: null,
    ctx: null,
    width: 640,
    height: 480,
};

/**
 * 启动摄像头
 * @param {string} facingMode - "user" | "environment" | ""（空=自动）
 * @param {number} width
 * @param {number} height
 * @returns {boolean} 是否成功
 */
export async function startCamera(facingMode, width, height) {
    stopCamera();

    _state.width = width || 640;
    _state.height = height || 480;

    const constraints = {
        video: facingMode ? { facingMode: { ideal: facingMode } } : true,
        audio: false,
    };

    try {
        _state.stream = await navigator.mediaDevices.getUserMedia(constraints);
    } catch {
        return false;
    }

    _state.video = document.createElement('video');
    _state.video.srcObject = _state.stream;
    _state.video.playsInline = true;
    _state.video.muted = true;
    await _state.video.play();

    _state.canvas = document.createElement('canvas');
    _state.canvas.width = _state.width;
    _state.canvas.height = _state.height;
    _state.ctx = _state.canvas.getContext('2d', { willReadFrequently: true });

    return true;
}

/**
 * 停止摄像头
 */
export function stopCamera() {
    if (_state.stream) {
        _state.stream.getTracks().forEach(t => t.stop());
    }
    _state.stream = null;
    _state.video = null;
    _state.canvas = null;
    _state.ctx = null;
}

/**
 * 捕获当前帧为 RGBA 字节数组
 * @returns {Uint8Array | null}
 */
export function getFrameData() {
    if (!_state.ctx || !_state.video) return null;
    _state.ctx.drawImage(_state.video, 0, 0, _state.width, _state.height);
    const imageData = _state.ctx.getImageData(0, 0, _state.width, _state.height);
    // Uint8ClampedArray → Uint8Array（.NET byte[] 需要 Uint8Array）
    return new Uint8Array(imageData.data.buffer);
}

/**
 * 拍照（返回 data URL 供 C# 侧解码，避免 Task<byte[]> 编组限制）
 * @returns {string} data:image/jpeg;base64,... 或空字符串
 */
export function capturePhoto() {
    if (!_state.ctx || !_state.video) return '';
    _state.ctx.drawImage(_state.video, 0, 0, _state.width, _state.height);
    return _state.canvas.toDataURL('image/jpeg', 0.92);
}
