@echo off
chcp 65001 >nul
title Avaliacao do RAG e dos agentes
cd /d "%~dp0"

echo ============================================
echo   Avaliacao do RAG e dos agentes
echo ============================================
echo.

echo [1/5] Verificando o .NET...
where dotnet >nul 2>nul
if errorlevel 1 (
  echo   [ERRO] .NET SDK nao encontrado. Instale o .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
  goto fim
)
echo   OK

echo [2/5] Verificando o Claude Code...
where claude.exe >nul 2>nul
if not errorlevel 1 goto claude_ok
where claude.cmd >nul 2>nul
if not errorlevel 1 (
  set "ClaudeCode__Command=claude.cmd"
  goto claude_ok
)
echo   [ERRO] Claude Code nao encontrado. Instale e rode "claude" uma vez para fazer login.
goto fim
:claude_ok
echo   OK

echo [3/5] Verificando o modelo de embeddings no Ollama...
where ollama >nul 2>nul
if errorlevel 1 (
  echo   [ERRO] Ollama nao encontrado.
  goto fim
)
ollama pull nomic-embed-text
if errorlevel 1 (
  echo   [ERRO] Nao consegui falar com o Ollama. Abra o app do Ollama e tente de novo.
  goto fim
)

if not exist eval\resultados mkdir eval\resultados
cd src\RagApi

echo.
echo [4/5] Avaliacao do RAG: 24 perguntas (5 a 10 minutos)...
echo.
powershell -NoProfile -Command "dotnet run --launch-profile claude-code -- eval 2>&1 | Tee-Object -FilePath ..\..\eval\resultados\log-rag.txt"

echo.
echo [5/5] Avaliacao dos agentes: 21 perguntas (5 a 10 minutos)...
echo.
powershell -NoProfile -Command "dotnet run --launch-profile claude-code -- eval --agentes 2>&1 | Tee-Object -FilePath ..\..\eval\resultados\log-agentes.txt"

echo.
echo Pronto! Os relatorios estao na pasta eval\resultados
echo FIM> ..\..\eval\resultados\terminou.txt

:fim
echo.
pause
