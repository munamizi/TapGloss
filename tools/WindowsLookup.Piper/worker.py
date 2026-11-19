"""离线合成 Amy 单词语音，并在标准输出逐行发送就绪、音频或失败消息。"""

import base64
import io
import json
import os
from pathlib import Path
import sys
import traceback
import wave


def _load_amy():
    """加载与试听页相同的 Amy 模型，限制 CPU 线程并让线程在空闲时休眠。"""
    import onnxruntime
    from piper import PiperVoice
    from piper.config import PiperConfig

    voice_directory = Path(__file__).resolve().parent
    model = voice_directory / "en_US-amy-medium.onnx"
    # eSpeak 通过相对路径读取发音数据，支持带中文的发布目录。
    os.chdir(voice_directory)
    espeak_data = Path("packages", "piper", "espeak-ng-data")
    config = PiperConfig.from_dict(
        json.loads(model.with_suffix(".onnx.json").read_text(encoding="utf-8"))
    )
    options = onnxruntime.SessionOptions()
    options.intra_op_num_threads = 2
    options.add_session_config_entry("session.intra_op.allow_spinning", "0")
    session = onnxruntime.InferenceSession(
        str(model), sess_options=options, providers=["CPUExecutionProvider"]
    )
    return PiperVoice(config=config, session=session, espeak_data_dir=espeak_data)


def _serve(voice, protocol):
    """复用同一模型，单次合成异常只结束该请求。"""
    for line in sys.stdin:
        word = line.rstrip("\r\n")
        if not word or len(word) > 80 or any(character.isspace() for character in word):
            print("ERR", file=protocol, flush=True)
            continue

        try:
            with io.BytesIO() as buffer:
                with wave.open(buffer, "wb") as audio:
                    voice.synthesize_wav(word, audio)
                encoded = base64.b64encode(buffer.getvalue()).decode("ascii")
        except Exception:
            traceback.print_exc(file=sys.stderr)
            print("ERR", file=protocol, flush=True)
            continue
        print("WAV:" + encoded, file=protocol, flush=True)


def _main():
    """将第三方库和 Python 输出转到 stderr，向原 stdout 管道发送协议。"""
    with os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="ascii", newline="\n") as protocol:
        os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
        voice = _load_amy()
        print("READY", file=protocol, flush=True)
        _serve(voice, protocol)


if __name__ == "__main__":
    _main()

