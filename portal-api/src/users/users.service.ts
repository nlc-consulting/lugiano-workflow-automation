import { Injectable } from '@nestjs/common';
import { Prisma } from '../generated/prisma';
import { PrismaService } from '../prisma/prisma.service';

// Fields safe to expose to the portal — never the password or refresh-token hashes.
const safeSelect = {
  id: true,
  email: true,
  fullName: true,
  role: true,
  office: true,
  isActive: true,
  createdAt: true,
} satisfies Prisma.UserSelect;

@Injectable()
export class UsersService {
  constructor(private readonly prisma: PrismaService) {}

  async findMany(skip: number, take: number, field: string, order: 'asc' | 'desc') {
    const [data, total] = await this.prisma.$transaction([
      this.prisma.user.findMany({
        skip,
        take,
        orderBy: { [field]: order },
        select: safeSelect,
      }),
      this.prisma.user.count(),
    ]);
    return { data, total };
  }

  findOne(id: number) {
    return this.prisma.user.findUnique({ where: { id }, select: safeSelect });
  }
}
