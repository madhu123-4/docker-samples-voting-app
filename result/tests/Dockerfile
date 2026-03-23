

FROM mcr.microsoft.com/playwright:v1.44.0-jammy

WORKDIR /app

COPY package.json ./

RUN npm install

COPY . .

# tests.sh waits for services, casts a vote, then runs the JS test
CMD ["/bin/sh", "/app/tests.sh"]