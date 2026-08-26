FROM node:22-alpine AS build
WORKDIR /app
ARG STORYVOICE_BASE_PATH=/
ENV STORYVOICE_BASE_PATH=${STORYVOICE_BASE_PATH}
COPY src/StoryVoice.Web/package*.json ./
RUN npm ci
COPY src/StoryVoice.Web/ ./
RUN npm run build

FROM nginx:1.29-alpine AS final
COPY docker/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
