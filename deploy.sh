#!/bin/bash

echo "====================================="
echo "🚀 Starting Deployment"
echo "====================================="

# 1. Create Docker network
echo "📡 Creating network vote-net..."
docker network create vote-net 2>/dev/null
docker network ls
echo "-------------------------------------"

# 2. Create PostgreSQL volume
echo "💾 Creating PostgreSQL volume..."
docker volume create pg_data
docker volume ls
echo "-------------------------------------"

# 3. Start PostgreSQL container
echo "🐘 Starting PostgreSQL container..."
docker run -d --name db --network vote-net \
  -e POSTGRES_USER=postgres \
  -e POSTGRES_PASSWORD=postgres \
  -e POSTGRES_DB=postgres \
  -v pg_data:/var/lib/postgresql/data \
  postgres:16-alpine

echo "⏳ Waiting for Postgres to be ready..."
sleep 5

docker ps
echo "-------------------------------------"

# 4. Build and run Vote service
echo "🗳️ Building Vote service..."
docker build -t vote ./vote

echo "🗳️ Starting Vote container..."
docker run -d --name vote --network vote-net -p 8080:80 \
  -e REDIS_HOST=redis \
  -e OPTION_A=Cats \
  -e OPTION_B=Dogs \
  vote

docker ps
echo "-------------------------------------"

# 5. Build and run Worker service
echo "⚙️ Building Worker service..."
docker build -t worker ./worker

echo "⚙️ Starting Worker container..."
docker run -d --name worker --network vote-net \
  -e REDIS_HOST=redis \
  -e DB_HOST=db \
  -e DB_USER=postgres \
  -e DB_PASS=postgres \
  worker

docker ps
echo "-------------------------------------"

# 6. Build and run Result service
echo "📊 Building Result service..."
docker build -t result ./result

echo "📊 Starting Result container..."
docker run -d --name result --network vote-net -p 8081:80 \
  -e DB_HOST=db \
  -e DB_USER=postgres \
  -e DB_PASS=postgres \
  -e PORT=80 \
  result

docker ps
echo "-------------------------------------"

echo "====================================="
echo "🎉 Deployment Completed Successfully!"
echo "👉 Vote UI:     http://localhost:8080"
echo "👉 Result UI:   http://localhost:8081"
echo "====================================="

