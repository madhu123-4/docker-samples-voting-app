FROM python:3.12-slim
WORKDIR /app

# Install deps first (layer cache)
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Copy source
COPY . .

EXPOSE 80

# Gunicorn: 4 sync workers, 30s timeout
CMD ["gunicorn", "--bind", "0.0.0.0:80", "--workers", "4", "--timeout", "30", "app:app"]