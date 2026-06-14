/**
 * CameraView Browser Helper
 *
 * 使用方式：在 main.js 中用 import './cameraView.js' 加载即可。
 * 所有函数注册到 globalThis，供 C# [JSImport] 以空模块名 "" 调用。
 */

(function () {

const _state = {
    stream: null,
    video: null,
    canvas: null,
    ctx: null,
    width: 640,
    height: 480,
};

globalThis.startCamera = async function (facingMode, width, height) {
    stopCamera();

    _state.width = width || 640;
    _state.height = height || 480;

    const constraints = {
        video: facingMode ? { facingMode: { ideal: facingMode } } : true,
        audio: false,
    };

    try {
        _state.stream = await navigator.mediaDevices.getUserMedia(constraints);
    } catch (e) {
        console.warn('CameraView: getUserMedia failed', e);
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
};

globalThis.stopCamera = function () {
    if (_state.stream) {
        _state.stream.getTracks().forEach(t => t.stop());
    }
    _state.stream = null;
    _state.video = null;
    _state.canvas = null;
    _state.ctx = null;
};

globalThis.getFrameData = function () {
    if (!_state.ctx || !_state.video) return null;
    _state.ctx.drawImage(_state.video, 0, 0, _state.width, _state.height);
    const imageData = _state.ctx.getImageData(0, 0, _state.width, _state.height);
    return new Uint8Array(imageData.data.buffer);
};

globalThis.capturePhoto = function () {
    if (!_state.ctx || !_state.video) return '';
    _state.ctx.drawImage(_state.video, 0, 0, _state.width, _state.height);
    return _state.canvas.toDataURL('image/jpeg', 0.92);
};

})();
