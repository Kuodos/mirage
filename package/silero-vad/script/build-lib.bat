@echo off 

rmdir /s /q "../bin"
mkdir "../bin"
call cl /Fo../bin/ /nologo /MT /LD /Fe../bin/silero-vad-api.dll /I ../../../lib/onnxruntime/include ../lib/silero-vad.c
del "../bin/silero-vad.obj"