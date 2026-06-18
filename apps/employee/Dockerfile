FROM node:22-alpine AS build
WORKDIR /app
COPY package.json angular.json tsconfig.json ./
COPY apps/employee ./apps/employee
COPY ["alldigi photo", "./alldigi photo"]
RUN npm install --cache /tmp/npm-cache
RUN npm run build:employee

FROM nginx:1.27-alpine
COPY apps/employee/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/employee/browser /usr/share/nginx/html
