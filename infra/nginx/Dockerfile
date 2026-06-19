# Stage 1: Build admin Angular app
FROM node:22-alpine AS admin-build
WORKDIR /app
COPY package.json angular.json tsconfig.json ./
COPY apps/admin ./apps/admin
COPY ["alldigi photo", "./alldigi photo"]
RUN npm install --cache /tmp/npm-cache
RUN npm run build:admin

# Stage 2: Build employee Angular app
FROM node:22-alpine AS employee-build
WORKDIR /app
COPY package.json angular.json tsconfig.json ./
COPY apps/employee ./apps/employee
COPY ["alldigi photo", "./alldigi photo"]
RUN npm install --cache /tmp/npm-cache
RUN npm run build:employee

# Final stage: Nginx serving both apps
FROM nginx:1.27-alpine
COPY --from=admin-build /app/dist/admin/browser /usr/share/nginx/html/admin
COPY --from=employee-build /app/dist/employee/browser /usr/share/nginx/html
COPY infra/nginx/nginx.conf /etc/nginx/nginx.conf
EXPOSE 80
