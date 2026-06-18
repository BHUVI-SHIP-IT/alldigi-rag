FROM node:22-alpine AS build
WORKDIR /app
COPY package.json angular.json tsconfig.json ./
COPY apps/admin ./apps/admin
COPY ["alldigi photo", "./alldigi photo"]
RUN npm install --cache /tmp/npm-cache
RUN npm run build:admin

FROM nginx:1.27-alpine
COPY apps/admin/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/admin/browser /usr/share/nginx/html/admin
