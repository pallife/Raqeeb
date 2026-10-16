FROM python:3.12-slim
WORKDIR /app
COPY relay/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY relay/raqeeb_relay.py .
COPY pwa /pwa
ENV RAQEEB_RELAY_STATIC=/pwa
RUN useradd -r -u 10001 raqeeb
USER raqeeb
EXPOSE 8080
CMD ["uvicorn", "raqeeb_relay:app", "--host", "0.0.0.0", "--port", "8080", "--no-access-log", "--log-level", "warning", "--ws-max-size", "4194304"]
