import { Injectable, UnauthorizedException } from '@nestjs/common';
import { JwtService, type JwtSignOptions } from '@nestjs/jwt';
import * as bcrypt from 'bcryptjs';
import { PrismaService } from '../prisma/prisma.service';

@Injectable()
export class AuthService {
  constructor(
    private readonly jwtService: JwtService,
    private readonly prisma: PrismaService,
  ) {}

  private getRefreshTtl() {
    return process.env.REFRESH_TOKEN_EXPIRES_IN || '7d';
  }

  private parseDurationToMs(value: string) {
    const normalized = value.trim();
    const match = normalized.match(/^(\d+)([smhd])$/);
    if (match) {
      const amount = Number(match[1]);
      const unit = match[2];
      const multipliers: Record<string, number> = {
        s: 1000,
        m: 60 * 1000,
        h: 60 * 60 * 1000,
        d: 24 * 60 * 60 * 1000,
      };
      return amount * multipliers[unit];
    }

    const numeric = Number(normalized);
    if (!Number.isNaN(numeric) && numeric > 0) {
      return numeric;
    }

    throw new Error(`Invalid REFRESH_TOKEN_EXPIRES_IN value: ${value}`);
  }

  private async issueTokens(user: { id: number; email: string; role: string }) {
    const accessPayload = {
      sub: String(user.id),
      email: user.email,
      role: user.role,
    };
    const accessToken = await this.jwtService.signAsync(accessPayload);

    const refreshPayload = {
      sub: String(user.id),
      token_type: 'refresh',
    };
    const refreshToken = await this.jwtService.signAsync(refreshPayload, {
      expiresIn: this.getRefreshTtl() as JwtSignOptions['expiresIn'],
    });

    const refreshTokenHash = await bcrypt.hash(refreshToken, 10);
    const refreshTokenExpiresAt = new Date(
      Date.now() + this.parseDurationToMs(this.getRefreshTtl()),
    );

    await this.prisma.user.update({
      where: { id: user.id },
      data: { refreshTokenHash, refreshTokenExpiresAt },
    });

    return { access_token: accessToken, refresh_token: refreshToken };
  }

  private async validateUser(username: string, password: string) {
    const normalizedEmail = username.trim().toLowerCase();
    const user = await this.prisma.user.findUnique({
      where: { email: normalizedEmail },
    });

    if (!user || !user.isActive) {
      return null;
    }

    const matches = await bcrypt.compare(password, user.passwordHash);
    if (!matches) {
      return null;
    }

    return user;
  }

  async login(username: string, password: string) {
    const user = await this.validateUser(username, password);
    if (!user) {
      throw new UnauthorizedException('Invalid username or password');
    }

    return this.issueTokens(user);
  }

  async refresh(refreshToken: string) {
    let payload: { sub?: string; token_type?: string };
    try {
      payload = await this.jwtService.verifyAsync(refreshToken);
    } catch {
      throw new UnauthorizedException('Invalid refresh token');
    }

    if (!payload?.sub || payload.token_type !== 'refresh') {
      throw new UnauthorizedException('Invalid refresh token');
    }

    const userId = Number(payload.sub);
    if (!Number.isFinite(userId)) {
      throw new UnauthorizedException('Invalid refresh token');
    }

    const user = await this.prisma.user.findUnique({ where: { id: userId } });
    if (!user || !user.isActive || !user.refreshTokenHash) {
      throw new UnauthorizedException('Invalid refresh token');
    }

    if (user.refreshTokenExpiresAt && user.refreshTokenExpiresAt < new Date()) {
      throw new UnauthorizedException('Refresh token expired');
    }

    const matches = await bcrypt.compare(refreshToken, user.refreshTokenHash);
    if (!matches) {
      throw new UnauthorizedException('Invalid refresh token');
    }

    return this.issueTokens(user);
  }
}
