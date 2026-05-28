import {
  createParamDecorator,
  ExecutionContext,
  UnauthorizedException,
} from '@nestjs/common';

export const UserId = createParamDecorator(
  (_data: unknown, ctx: ExecutionContext) => {
    const request = ctx.switchToHttp().getRequest<{ user?: { userId?: string } }>();
    const userId = request?.user?.userId;
    if (!userId) {
      throw new UnauthorizedException('User not found');
    }
    return Number(userId);
  },
);
