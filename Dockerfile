# Use Node.js as base
FROM node:20-slim

# Install sqlite3 for safe backups
RUN apt-get update && apt-get install -y sqlite3 && rm -rf /var/lib/apt/lists/*

# Create app directory
WORKDIR /usr/src/app

# Copy package files and install
COPY package*.json ./
RUN npm install --production

# Copy source
COPY . .

# Set default env
ENV OPENCLAW_HOME=/root/.openclaw
ENV BACKUP_DIR=/backups

# Link the CLI
RUN npm link

# Default command
CMD ["reclaw", "--help"]
