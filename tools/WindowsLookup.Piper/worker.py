"""用应用自带的 Amy 模型离线合成单词，在标准输出逐行返回 WAV。"""

import base64
import io
import json
import os
from pathlib import Path
import sys
import wave

import onnxruntime
from piper import PiperVoice
from piper.config import PiperConfig


def _load_amy():
    """加载与试听页相同的 Amy 模型，限制 CPU 线程并让线程在空闲时休眠。"""
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


def _serve(voice):
    """复用同一模型处理输入管道，父进程关闭输入后正常退出。"""
    for line in sys.stdin:
        word = line.rstrip("\r\n")
        if not word or len(word) > 80 or any(character.isspace() for character in word):
            print("")
            continue

        with io.BytesIO() as buffer:
            with wave.open(buffer, "wb") as audio:
                voice.synthesize_wav(word, audio)
            print(base64.b64encode(buffer.getvalue()).decode("ascii"))


if __name__ == "__main__":
    _serve(_load_amy())

