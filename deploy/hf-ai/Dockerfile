FROM python:3.12-slim
ARG BRANCH=wave-3-to-5-complete
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 --branch ${BRANCH} https://github.com/shafiqblouch/UrbanFixAI.git /repo
WORKDIR /repo/ai
RUN pip install --no-cache-dir .
EXPOSE 7860
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "7860"]
