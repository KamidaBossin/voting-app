FROM node:24-alpine

WORKDIR /app

ENV NODE_ENV=production

COPY package*.json ./
RUN npm ci --omit=dev

USER node

COPY --chown=node:node . .

EXPOSE 8888

CMD ["node", "server.js"]
