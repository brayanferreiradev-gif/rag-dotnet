@echo off
chcp 65001 >nul
title Avaliacao do RAG
cd /d "%~dp0"

echo ============================================
echo   Avaliacao do RAG (Claude Code + Ollama)
echo ============================================
echo.

echo [1/4] Verificando o .NET...
where dotnet >nul 2>nul
if errorlevel 1 (
  echo   [ERRO] .NET SDK nao encontrado.
  echo   Instale o .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
  goto fim
)
echo   OK

echo [2/4] Verificando o Claude Code...
where claude.exe >nul 2>nul
if not errorlevel 1 goto claude_ok
where claude.cmd >nul 2>nul
if not errorlevel 1 (
  set "ClaudeCode__Command=claude.cmd"
  goto claude_ok
)
echo   [ERRO] Claude Code nao encontrado.
echo   Instale: https://code.claude.com/docs/en/setup  e rode "claude" uma vez para fazer login.
goto fim
:claude_ok
echo   OK

echo [3/4] Baixando o modelo de embeddings no Ollama (so na primeira vez)...
where ollama >nul 2>nul
if errorlevel 1 (
  echo   [ERRO] Ollama nao encontrado. Feche e abra de novo depois de instalar, ou reinicie o PC.
  goto fim
)
ollama pull nomic-embed-text
if errorlevel 1 (
  echo   [ERRO] Nao consegui falar com o Ollama. Abra o app do Ollama e tente de novo.
  goto fim
)

echo.
echo [4/4] Rodando a avaliacao: 24 perguntas, leva de 5 a 10 minutos...
echo.
cd src\RagApi
dotnet run --launch-profile claude-code -- eval
echo.
if errorlevel 1 (
  echo Terminou com erros. Copie a mensagem acima e me mande.
) else (
  echo Pronto! O relatorio esta na pasta eval\resultados
)

:fim
echo.
pause
