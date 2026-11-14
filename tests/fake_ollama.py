"""
Ollama falso para testar o pipeline sem baixar modelos.

- /api/embed: embedding determinístico (bag-of-words com hashing, 256 dims),
  bom o bastante para que perguntas com as mesmas palavras achem o trecho certo.
- /api/chat: devolve o primeiro trecho do contexto, para provar que o prompt
  chegou montado corretamente.

Uso: python3 tests/fake_ollama.py 11434
"""
import hashlib
import json
import math
import re
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

DIMS = 256
STOP = {"o", "a", "os", "as", "de", "do", "da", "dos", "das", "e", "em", "no", "na",
        "um", "uma", "para", "por", "com", "que", "qual", "quais", "como", "é", "se", "ou"}


def embed(text: str) -> list[float]:
    vec = [0.0] * DIMS
    for word in re.findall(r"\w+", text.lower()):
        if word in STOP or len(word) < 3:
            continue
        stem = word[:6]  # "radical" grosseiro: troca/trocas/trocar caem juntos
        h = int(hashlib.md5(stem.encode()).hexdigest(), 16)
        vec[h % DIMS] += 1.0
    norm = math.sqrt(sum(v * v for v in vec)) or 1.0
    return [v / norm for v in vec]


class Handler(BaseHTTPRequestHandler):
    # HTTP/1.1 mantém a conexão aberta, como o HttpClient do .NET espera ao reaproveitar conexões.
    protocol_version = "HTTP/1.1"

    def _send(self, payload, status=200):
        data = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _read_body(self) -> bytes:
        if self.headers.get("Content-Length"):
            return self.rfile.read(int(self.headers["Content-Length"]))
        # HttpClient do .NET envia JSON com Transfer-Encoding: chunked.
        data = b""
        while True:
            size = int(self.rfile.readline().strip(), 16)
            if size == 0:
                self.rfile.readline()
                return data
            data += self.rfile.read(size)
            self.rfile.readline()

    def do_POST(self):
        body = json.loads(self._read_body())
        if self.path == "/api/embed":
            inputs = body["input"] if isinstance(body["input"], list) else [body["input"]]
            self._send({"model": body["model"], "embeddings": [embed(t) for t in inputs]})
        elif self.path == "/api/chat":
            system = body["messages"][0]["content"]
            user = body["messages"][-1]["content"]
            if "reescrever" in system:
                # Etapa de reescrita: junta a última pergunta do usuário com a nova (bom o bastante p/ teste).
                lines = [l for l in user.splitlines() if l.startswith("Usuário: ")]
                prev = lines[-1].removeprefix("Usuário: ") if lines else ""
                new = user.split("Última pergunta:", 1)[1].strip()
                content = f"{new} ({prev})" if prev else new
            elif "[1]" in user:
                first = user.split("[1]", 1)[1].split("\n\n[2]", 1)[0].split("Pergunta:")[0].strip()
                content = f"[FAKE] Com base em [1]: {first[:200]}"
            else:
                content = "[FAKE] sem contexto"
            self._send({"model": body["model"], "done": True,
                        "message": {"role": "assistant", "content": content}})
        else:
            self._send({"error": "not found"}, 404)

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 11434
    ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
